using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chirpdesk.Core.Logging;

namespace Chirpdesk.Core.Integrations;

public sealed record IntegrationSessionOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(3);

    public int MaxProtocolErrors { get; init; } = 5;

    public string AppVersion { get; init; } = "0.0.0";

    public IReadOnlyList<TimeSpan> RestartDelays { get; init; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
    ];
}

/// <summary>
/// Owns one helper lifecycle: launch, handshake, dispatch, command correlation, timeouts and
/// restart backoff. The OS process and pipe live behind <see cref="IIntegrationLauncher"/>; this
/// class holds every decision and is therefore testable without a child process.
/// </summary>
public sealed class IntegrationSession : IAsyncDisposable
{
    private readonly IntegrationManifest _manifest;
    private readonly string _pluginId;
    private readonly string _executablePath;
    private readonly string _dataDirectory;
    private readonly IReadOnlySet<string> _permissions;
    private readonly IIntegrationLauncher _launcher;
    private readonly IIntegrationDelay _delay;
    private readonly ILog? _log;
    private readonly IntegrationSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcEnvelope>> _pending = new();
    private readonly object _stateLock = new();

    private IIntegrationProcess? _process;
    private CancellationTokenSource? _runCts;
    private IntegrationState _state;
    private string? _lastError;
    private bool _stopRequested;
    private bool _disposed;
    private int _protocolErrors;
    private int _restartAttempt;
    private long _nextRequestId;

    public IntegrationSession(
        IntegrationManifest manifest,
        string pluginId,
        string executablePath,
        string dataDirectory,
        IReadOnlySet<string> permissions,
        IIntegrationLauncher launcher,
        IIntegrationDelay? delay = null,
        ILog? log = null,
        IntegrationSessionOptions? options = null)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        _executablePath = executablePath ?? throw new ArgumentNullException(nameof(executablePath));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _delay = delay ?? new SystemIntegrationDelay();
        _log = log;
        _options = options ?? new IntegrationSessionOptions();
    }

    /// <summary>Raised on whatever thread changed the state; UI code must marshal.</summary>
    public event EventHandler<IntegrationStateChanged>? StateChanged;

    public event Action<IntegrationNotifyMessage>? NotificationReceived;

    public event Action<int>? UnreadChanged;

    public event Action<IntegrationStatusMessage>? HelperStatus;

    public IntegrationState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastError;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_state is IntegrationState.Running or IntegrationState.Starting or IntegrationState.Stopping)
            {
                return;
            }

            _stopRequested = false;
            _restartAttempt = 0;
            _protocolErrors = 0;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();

            await StartCoreAsync(_runCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_state == IntegrationState.Stopped)
            {
                return;
            }

            _stopRequested = true;

            if (_state is IntegrationState.Running or IntegrationState.Starting)
            {
                SetState(IntegrationState.Stopping, null);
            }

            var process = _process;

            if (process is not null)
            {
                try
                {
                    await WriteAsync(JsonRpcCodec.Notification("shutdown"), CancellationToken.None)
                        .WaitAsync(_options.ShutdownGrace, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    // A helper that cannot take a graceful stop is killed below.
                }

                try
                {
                    await process.WaitForExitAsync(cancellationToken)
                        .WaitAsync(_options.ShutdownGrace, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
                {
                    process.Kill();
                }

                await process.DisposeAsync().ConfigureAwait(false);
                _process = null;
            }

            _runCts?.Cancel();
            FailPending("helper stopped");
            SetState(IntegrationState.Stopped, null);
            _runCts?.Dispose();
            _runCts = null;
            _restartAttempt = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Marks the session unusable without starting a process (e.g. a changed executable).</summary>
    public void Fail(string reason)
    {
        _stopRequested = true;
        SetState(IntegrationState.Error, reason);
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        if (State != IntegrationState.Running)
        {
            return false;
        }

        try
        {
            var response = await SendRequestAsync("ping", null, _options.PingTimeout, cancellationToken)
                .ConfigureAwait(false);

            return response.Error is null;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<IntegrationCommandResult> SendCommandAsync(
        string id,
        string? argument,
        CancellationToken cancellationToken = default)
    {
        if (State != IntegrationState.Running)
        {
            return IntegrationCommandResult.Failure("helper is not running");
        }

        var command = _manifest.Commands.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));

        if (command is null)
        {
            return IntegrationCommandResult.Failure("command is not declared");
        }

        if (string.Equals(command.Argument, "none", StringComparison.Ordinal) && !string.IsNullOrEmpty(argument))
        {
            return IntegrationCommandResult.Failure("command takes no argument");
        }

        if (string.Equals(command.Argument, "text", StringComparison.Ordinal) && string.IsNullOrEmpty(argument))
        {
            return IntegrationCommandResult.Failure("command needs text");
        }

        try
        {
            var response = await SendRequestAsync(
                "command",
                new { id = command.Id, argument },
                _options.CommandTimeout,
                cancellationToken).ConfigureAwait(false);

            if (response.Error is not null)
            {
                return IntegrationCommandResult.Failure(response.Error.Message);
            }

            if (response.Result.ValueKind == JsonValueKind.Object)
            {
                if (response.Result.TryGetProperty("ok", out var ok)
                    && ok.ValueKind == JsonValueKind.False)
                {
                    var message = response.Result.TryGetProperty("message", out var messageElement)
                        && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null;

                    return new IntegrationCommandResult(false, message, message ?? "command failed");
                }

                if (response.Result.TryGetProperty("message", out var note)
                    && note.ValueKind == JsonValueKind.String)
                {
                    return new IntegrationCommandResult(true, note.GetString(), null);
                }
            }

            return new IntegrationCommandResult(true, null, null);
        }
        catch (TimeoutException)
        {
            return IntegrationCommandResult.Failure("command timed out");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return IntegrationCommandResult.Failure("helper connection closed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
        }

        _disposed = true;
        _gate.Dispose();
        _writeLock.Dispose();
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        SetState(IntegrationState.Starting, null);

        var token = CreateToken();
        var pipeName = $"chirpdesk-integration-{Guid.NewGuid():N}";

        try
        {
            Directory.CreateDirectory(_dataDirectory);

            _process = await _launcher.StartAsync(
                new IntegrationLaunchRequest(
                    _pluginId,
                    _executablePath,
                    _dataDirectory,
                    pipeName,
                    token,
                    IntegrationProtocol.Version),
                cancellationToken).ConfigureAwait(false);

            var frame = await ReadFrameAsync(_process.Connection, _options.HandshakeTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (frame is null)
            {
                await FailStartAsync("helper did not send an initialize request").ConfigureAwait(false);
                return;
            }

            if (!JsonRpcCodec.TryParse(frame, out var envelope, out var parseError) || envelope is null)
            {
                await FailStartAsync($"initialize is not valid json-rpc: {parseError}").ConfigureAwait(false);
                return;
            }

            if (envelope.Kind != JsonRpcKind.Request
                || !string.Equals(envelope.Method, "initialize", StringComparison.Ordinal))
            {
                await FailStartAsync("the first frame must be an initialize request").ConfigureAwait(false);
                return;
            }

            if (!IntegrationProtocol.TryReadInitialize(envelope.Params, out var initialize, out var initializeError)
                || initialize is null)
            {
                await FailStartAsync($"bad initialize parameters: {initializeError}").ConfigureAwait(false);
                return;
            }

            if (!TokenMatches(token, initialize.Token))
            {
                await FailStartAsync("initialize token does not match").ConfigureAwait(false);
                return;
            }

            if (initialize.ProtocolVersion != IntegrationProtocol.Version)
            {
                await FailStartAsync(
                    $"helper speaks protocol {initialize.ProtocolVersion}; the host speaks {IntegrationProtocol.Version}")
                    .ConfigureAwait(false);
                return;
            }

            await WriteAsync(
                JsonRpcCodec.Response(envelope.Id, new
                {
                    protocolVersion = IntegrationProtocol.Version,
                    appVersion = _options.AppVersion,
                    permissions = _permissions.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                    dataDirectory = _dataDirectory,
                }),
                cancellationToken).ConfigureAwait(false);

            SetState(IntegrationState.Running, null);
            _ = Task.Run(() => ReadLoopAsync(_process, cancellationToken), CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            await FailStartAsync($"could not start helper: {ex.Message}").ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(IIntegrationProcess process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[]? frame;

                try
                {
                    frame = await IntegrationFraming.ReadAsync(process.Connection, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    await StopWithErrorAsync($"framing: {ex.Message}").ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    break;
                }

                if (frame is null)
                {
                    break;
                }

                if (!await HandleFrameAsync(frame, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_stopRequested)
        {
            return;
        }

        FailPending("helper stopped");
        await ScheduleRestartAsync(process).ConfigureAwait(false);
    }

    private async Task<bool> HandleFrameAsync(byte[] frame, CancellationToken cancellationToken)
    {
        if (!JsonRpcCodec.TryParse(frame, out var envelope, out var error) || envelope is null)
        {
            return await CountProtocolErrorAsync($"invalid json-rpc: {error}", cancellationToken).ConfigureAwait(false);
        }

        if (envelope.Kind == JsonRpcKind.Response)
        {
            if (_pending.TryRemove(envelope.Id, out var completion))
            {
                completion.TrySetResult(envelope);
            }
            else
            {
                _log?.Warn($"integration '{_pluginId}' sent a response nobody is waiting for");
            }

            return true;
        }

        if (string.Equals(envelope.Method, "initialize", StringComparison.Ordinal))
        {
            await ReplyErrorAsync(envelope, -32600, "already initialized", cancellationToken).ConfigureAwait(false);
            return await CountProtocolErrorAsync("duplicate initialize", cancellationToken).ConfigureAwait(false);
        }

        if (!IntegrationProtocol.IsHelperMethod(envelope.Method))
        {
            await ReplyErrorAsync(envelope, -32601, "method not found", cancellationToken).ConfigureAwait(false);
            return await CountProtocolErrorAsync($"method '{envelope.Method}' is not allowed", cancellationToken)
                .ConfigureAwait(false);
        }

        var permission = IntegrationProtocol.RequiredPermission(envelope.Method);

        if (permission is not null && !_permissions.Contains(permission))
        {
            await ReplyErrorAsync(envelope, -32603, "permission not granted", cancellationToken).ConfigureAwait(false);
            return await CountProtocolErrorAsync(
                $"method '{envelope.Method}' needs the '{permission}' permission",
                cancellationToken).ConfigureAwait(false);
        }

        switch (envelope.Method)
        {
            case "notify":
            {
                if (!IntegrationProtocol.TryReadNotify(envelope.Params, out var message, out var notifyError)
                    || message is null)
                {
                    await ReplyErrorAsync(envelope, -32602, notifyError ?? "invalid params", cancellationToken)
                        .ConfigureAwait(false);
                    return await CountProtocolErrorAsync($"notify: {notifyError}", cancellationToken).ConfigureAwait(false);
                }

                NotificationReceived?.Invoke(message);
                await ReplyAsync(envelope, new { }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "unread":
            {
                if (!IntegrationProtocol.TryReadUnread(envelope.Params, out var message, out var unreadError)
                    || message is null)
                {
                    await ReplyErrorAsync(envelope, -32602, unreadError ?? "invalid params", cancellationToken)
                        .ConfigureAwait(false);
                    return await CountProtocolErrorAsync($"unread: {unreadError}", cancellationToken).ConfigureAwait(false);
                }

                UnreadChanged?.Invoke(message.Count);
                await ReplyAsync(envelope, new { }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "status":
            {
                if (!IntegrationProtocol.TryReadStatus(envelope.Params, out var message, out var statusError)
                    || message is null)
                {
                    await ReplyErrorAsync(envelope, -32602, statusError ?? "invalid params", cancellationToken)
                        .ConfigureAwait(false);
                    return await CountProtocolErrorAsync($"status: {statusError}", cancellationToken).ConfigureAwait(false);
                }

                HelperStatus?.Invoke(message);
                await ReplyAsync(envelope, new { }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            case "log":
            {
                if (!IntegrationProtocol.TryReadLog(envelope.Params, out var message, out var logError)
                    || message is null)
                {
                    await ReplyErrorAsync(envelope, -32602, logError ?? "invalid params", cancellationToken)
                        .ConfigureAwait(false);
                    return await CountProtocolErrorAsync($"log: {logError}", cancellationToken).ConfigureAwait(false);
                }

                var line = message.Message
                    .Replace('\r', ' ')
                    .Replace('\n', ' ')
                    .Trim();

                if (string.Equals(message.Level, "error", StringComparison.Ordinal))
                {
                    _log?.Error($"integration '{_pluginId}': {line}");
                }
                else if (string.Equals(message.Level, "warn", StringComparison.Ordinal))
                {
                    _log?.Warn($"integration '{_pluginId}': {line}");
                }
                else
                {
                    _log?.Info($"integration '{_pluginId}': {line}");
                }

                await ReplyAsync(envelope, new { }, cancellationToken).ConfigureAwait(false);
                return true;
            }

            default:
                await ReplyErrorAsync(envelope, -32601, "method not found", cancellationToken).ConfigureAwait(false);
                return await CountProtocolErrorAsync($"unhandled method '{envelope.Method}'", cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    private async Task<bool> CountProtocolErrorAsync(string message, CancellationToken cancellationToken)
    {
        _protocolErrors++;
        _log?.Warn($"integration '{_pluginId}': {message}");

        if (_protocolErrors <= _options.MaxProtocolErrors)
        {
            return true;
        }

        await StopWithErrorAsync("too many protocol errors").ConfigureAwait(false);
        return false;
    }

    private async Task StopWithErrorAsync(string error)
    {
        _stopRequested = true;
        _log?.Warn($"integration '{_pluginId}' stopped: {error}");

        var process = _process;

        try
        {
            process?.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        if (process is not null)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            _process = null;
        }

        _runCts?.Cancel();
        FailPending(error);
        SetState(IntegrationState.Error, error);
    }

    private async Task FailStartAsync(string error)
    {
        _log?.Warn($"integration '{_pluginId}' failed to start: {error}");

        var process = _process;

        if (process is not null)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            await process.DisposeAsync().ConfigureAwait(false);
            _process = null;
        }

        SetState(IntegrationState.Error, error);
    }

    private async Task ScheduleRestartAsync(IIntegrationProcess process)
    {
        int attempt;
        CancellationToken token;

        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_stopRequested || _disposed)
            {
                return;
            }

            token = _runCts?.Token ?? CancellationToken.None;

            if (_restartAttempt >= _options.RestartDelays.Count)
            {
                SetState(IntegrationState.Error, "helper stopped repeatedly");
                return;
            }

            attempt = _restartAttempt++;
            SetState(IntegrationState.Starting, "restarting");
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await _delay.DelayAsync(_options.RestartDelays[attempt], token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await process.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_stopRequested || _disposed)
            {
                return;
            }

            _process = null;
            await StartCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonRpcEnvelope> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        var completion = new TaskCompletionSource<JsonRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await WriteAsync(JsonRpcCodec.Request(id, method, parameters), timeoutCts.Token).ConfigureAwait(false);

            try
            {
                return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"'{method}' timed out");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReplyAsync(JsonRpcEnvelope request, object? result, CancellationToken cancellationToken)
    {
        if (request.Kind != JsonRpcKind.Request)
        {
            return;
        }

        await WriteAsync(JsonRpcCodec.Response(request.Id, result), cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplyErrorAsync(
        JsonRpcEnvelope request,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        if (request.Kind != JsonRpcKind.Request)
        {
            return;
        }

        await WriteAsync(JsonRpcCodec.ErrorResponse(request.Id, code, message), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var process = _process ?? throw new IOException("helper is not running");
            await IntegrationFraming.WriteAsync(process.Connection, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await IntegrationFraming.ReadAsync(stream, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void FailPending(string error)
    {
        foreach (var entry in _pending)
        {
            entry.Value.TrySetException(new IOException(error));
        }

        _pending.Clear();
    }

    private void SetState(IntegrationState state, string? detail)
    {
        lock (_stateLock)
        {
            _state = state;

            if (!string.IsNullOrWhiteSpace(detail))
            {
                _lastError = detail;
            }
        }

        _log?.Info($"integration '{_pluginId}' state {state}");
        StateChanged?.Invoke(this, new IntegrationStateChanged(state, detail));
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static bool TokenMatches(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
