using System.Text;
using System.Threading.Channels;
using Chirpdesk.Core.Integrations;
using Chirpdesk.Core.Logging;

namespace Chirpdesk.Core.Tests.Integrations;

internal sealed class DuplexPair
{
    public static (Stream Host, Stream Helper) Create()
    {
        var hostToHelper = Channel.CreateUnbounded<byte[]>();
        var helperToHost = Channel.CreateUnbounded<byte[]>();

        var host = new ChannelStream(helperToHost.Reader, hostToHelper.Writer);
        var helper = new ChannelStream(hostToHelper.Reader, helperToHost.Writer);

        return (host, helper);
    }

    private sealed class ChannelStream(ChannelReader<byte[]> reader, ChannelWriter<byte[]> writer) : Stream
    {
        private byte[]? _current;
        private int _offset;
        private bool _disposed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (reader.TryRead(out var chunk))
                {
                    _current = chunk;
                    _offset = 0;
                }
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            writer.WriteAsync(buffer.ToArray(), cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}

internal sealed class FakeIntegrationProcess(Stream host, Stream helper) : IIntegrationProcess
{
    private int _exitCode;

    public Stream Connection { get; } = host;

    public bool HasExited { get; private set; }

    public int? ExitCode => HasExited ? _exitCode : null;

    public void Kill()
    {
        if (HasExited)
        {
            return;
        }

        HasExited = true;
        _exitCode = -1;
        helper.Dispose();
        Connection.Dispose();
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        if (HasExited)
        {
            return Task.CompletedTask;
        }

        return Task.Delay(Timeout.Infinite, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Kill();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeIntegrationLauncher : IIntegrationLauncher
{
    public Func<IntegrationLaunchRequest, Stream, Task>? Script { get; set; }

    public List<IntegrationLaunchRequest> Requests { get; } = [];

    public int Starts { get; private set; }

    public Task<IIntegrationProcess> StartAsync(
        IntegrationLaunchRequest request,
        CancellationToken cancellationToken)
    {
        Starts++;
        Requests.Add(request);

        var (host, helper) = DuplexPair.Create();
        var process = new FakeIntegrationProcess(host, helper);

        if (Script is not null)
        {
            _ = Task.Run(() => Script(request, helper), CancellationToken.None);
        }

        return Task.FromResult<IIntegrationProcess>(process);
    }
}

internal sealed class ImmediateIntegrationDelay : IIntegrationDelay
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Delays.Add(delay);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        return Task.CompletedTask;
    }
}

internal sealed class RecordingLog : ILog
{
    public List<string> Lines { get; } = [];

    public void Info(string message) => Lines.Add(message);

    public void Warn(string message) => Lines.Add(message);

    public void Error(string message, Exception? exception = null) => Lines.Add(message);
}

internal static class IntegrationTestHelpers
{
    public static async Task<JsonRpcEnvelope> InitializeAsync(
        Stream helper,
        IntegrationLaunchRequest request,
        string? token = null)
    {
        await WriteAsync(
            helper,
            JsonRpcCodec.Request(1, "initialize", new
            {
                protocolVersion = IntegrationProtocol.Version,
                token = token ?? request.Token,
            }));

        var response = await ReadAsync(helper);
        if (response is null || response.Kind != JsonRpcKind.Response)
        {
            throw new InvalidOperationException("no initialize response");
        }

        return response;
    }

    public static Task SendAsync(Stream helper, long id, string method, object? parameters = null) =>
        WriteAsync(helper, JsonRpcCodec.Request(id, method, parameters));

    public static Task SendNotificationAsync(Stream helper, string method, object? parameters = null) =>
        WriteAsync(helper, JsonRpcCodec.Notification(method, parameters));

    public static async Task<JsonRpcEnvelope?> ReadAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await IntegrationFraming.ReadAsync(stream, timeout.Token);

        if (frame is null)
        {
            return null;
        }

        return JsonRpcCodec.TryParse(frame, out var envelope, out var error)
            ? envelope
            : throw new InvalidOperationException(error);
    }

    public static Task WriteAsync(Stream stream, byte[] frame) =>
        IntegrationFraming.WriteAsync(stream, frame);

    public static async Task WaitForStateAsync(
        IntegrationSession session,
        IntegrationState state,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.State == state)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"session did not reach {state}; it is {session.State}");
    }

    public static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
