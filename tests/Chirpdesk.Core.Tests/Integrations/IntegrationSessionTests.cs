using Chirpdesk.Core.Integrations;

namespace Chirpdesk.Core.Tests.Integrations;

public sealed class IntegrationSessionTests
{
    private static readonly IntegrationManifest Manifest = new()
    {
        ProtocolVersion = 1,
        Executable = "helper.exe",
        Commands =
        [
            new IntegrationCommand { Id = "send", Title = "Send", Argument = "text" },
            new IntegrationCommand { Id = "open", Title = "Open", Argument = "none" },
        ],
    };

    private static readonly IntegrationSessionOptions FastOptions = new()
    {
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        CommandTimeout = TimeSpan.FromMilliseconds(250),
        PingTimeout = TimeSpan.FromMilliseconds(200),
        ShutdownGrace = TimeSpan.FromMilliseconds(100),
        AppVersion = "0.2.1",
        RestartDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
    };

    [Fact]
    public async Task A_handshake_reaches_running_with_the_granted_permissions()
    {
        var response = new TaskCompletionSource<JsonRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                response.TrySetResult(await IntegrationTestHelpers.InitializeAsync(helper, request));
            },
        };

        await using var session = Create(launcher);

        await session.StartAsync();

        Assert.Equal(IntegrationState.Running, session.State);

        var initialize = await response.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(JsonRpcKind.Response, initialize.Kind);
        Assert.True(initialize.Result.GetProperty("permissions").GetArrayLength() >= 1);
        Assert.Equal("0.2.1", initialize.Result.GetProperty("appVersion").GetString());

        await session.StopAsync();
    }

    [Fact]
    public async Task A_wrong_token_stops_the_helper_with_an_error()
    {
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request, token: "not-the-token");
            },
        };
        var log = new RecordingLog();

        await using var session = Create(launcher, log: log);
        await session.StartAsync();

        Assert.Equal(IntegrationState.Error, session.State);
        Assert.Contains("token", session.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log.Lines, line => line.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_handshake_timeout_is_an_error()
    {
        var launcher = new FakeIntegrationLauncher { Script = (_, _) => Task.Delay(Timeout.Infinite) };

        await using var session = Create(
            launcher,
            options: FastOptions with { HandshakeTimeout = TimeSpan.FromMilliseconds(100) });

        await session.StartAsync();

        Assert.Equal(IntegrationState.Error, session.State);
        Assert.Contains("initialize", session.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Notify_is_dispatched_and_acknowledged()
    {
        var received = new TaskCompletionSource<IntegrationNotifyMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgement = new TaskCompletionSource<JsonRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);
                await IntegrationTestHelpers.SendAsync(helper, 2, "notify", new
                {
                    title = "New message",
                    body = "body text",
                    tag = "chat",
                });
                acknowledgement.TrySetResult((await IntegrationTestHelpers.ReadAsync(helper))!);
            },
        };

        await using var session = Create(launcher);
        session.NotificationReceived += message => received.TrySetResult(message);

        await session.StartAsync();

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("New message", message.Title);
        Assert.Equal("body text", message.Body);

        var ack = await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, ack.Id);
        Assert.Null(ack.Error);

        await session.StopAsync();
    }

    [Fact]
    public async Task Notify_without_the_permission_is_refused()
    {
        var raised = false;
        var acknowledgement = new TaskCompletionSource<JsonRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);
                await IntegrationTestHelpers.SendAsync(helper, 2, "notify", new { title = "Hello" });
                acknowledgement.TrySetResult((await IntegrationTestHelpers.ReadAsync(helper))!);
            },
        };

        await using var session = Create(
            launcher,
            permissions: Permissions("process"),
            log: new RecordingLog());
        session.NotificationReceived += _ => raised = true;

        await session.StartAsync();

        var ack = await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(-32603, ack.Error!.Code);
        Assert.False(raised);

        await session.StopAsync();
    }

    [Fact]
    public async Task Unread_counts_reach_the_host()
    {
        var count = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);
                await IntegrationTestHelpers.SendAsync(helper, 2, "unread", new { count = 7 });
                await IntegrationTestHelpers.ReadAsync(helper);
            },
        };

        await using var session = Create(launcher);
        session.UnreadChanged += value => count.TrySetResult(value);

        await session.StartAsync();

        Assert.Equal(7, await count.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        await session.StopAsync();
    }

    [Fact]
    public async Task Log_lines_are_sanitised_and_a_notification_body_is_not_logged()
    {
        var acknowledgement = new TaskCompletionSource<JsonRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new RecordingLog();
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);
                await IntegrationTestHelpers.SendAsync(helper, 2, "log", new
                {
                    level = "info",
                    message = "first line\nsecond line",
                });
                await IntegrationTestHelpers.ReadAsync(helper);
                await IntegrationTestHelpers.SendAsync(helper, 3, "notify", new
                {
                    title = "Hi",
                    body = "secret body text",
                });
                acknowledgement.TrySetResult((await IntegrationTestHelpers.ReadAsync(helper))!);
            },
        };

        await using var session = Create(launcher, log: log);
        await session.StartAsync();

        await acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await session.StopAsync();

        Assert.Contains(log.Lines, line => line.Contains("first line second line", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains("secret body", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, line => line.Contains('\n'));
    }

    [Fact]
    public async Task A_declared_command_round_trips_with_its_argument()
    {
        var received = new TaskCompletionSource<(string Id, string? Argument)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);

                while (true)
                {
                    var envelope = await IntegrationTestHelpers.ReadAsync(helper);

                    if (envelope is null || envelope.Kind == JsonRpcKind.Notification)
                    {
                        return;
                    }

                    var id = envelope.Params.GetProperty("id").GetString()!;
                    var argument = envelope.Params.TryGetProperty("argument", out var argumentElement)
                        ? argumentElement.GetString()
                        : null;
                    received.TrySetResult((id, argument));

                    await IntegrationTestHelpers.WriteAsync(
                        helper,
                        JsonRpcCodec.Response(envelope.Id, new { ok = true, message = "sent" }));
                }
            },
        };

        await using var session = Create(launcher);
        await session.StartAsync();

        var result = await session.SendCommandAsync("send", "hello");

        Assert.True(result.Ok);
        Assert.Equal("sent", result.Message);
        var call = await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("send", call.Id);
        Assert.Equal("hello", call.Argument);

        await session.StopAsync();
    }

    [Fact]
    public async Task An_undeclared_command_is_refused_without_touching_the_helper()
    {
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) => await IntegrationTestHelpers.InitializeAsync(helper, request),
        };

        await using var session = Create(launcher);
        await session.StartAsync();

        var result = await session.SendCommandAsync("delete-everything", null);

        Assert.False(result.Ok);
        Assert.Contains("not declared", result.Error, StringComparison.OrdinalIgnoreCase);

        await session.StopAsync();
    }

    [Fact]
    public async Task A_command_reply_that_never_comes_times_out()
    {
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) => await IntegrationTestHelpers.InitializeAsync(helper, request),
        };

        await using var session = Create(
            launcher,
            options: FastOptions with { CommandTimeout = TimeSpan.FromMilliseconds(100) });
        await session.StartAsync();

        var result = await session.SendCommandAsync("send", "hello");

        Assert.False(result.Ok);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);

        await session.StopAsync();
    }

    [Fact]
    public async Task Repeated_crashes_use_the_backoff_and_end_in_error()
    {
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);
                helper.Dispose();
            },
        };
        var delay = new ImmediateIntegrationDelay();

        await using var session = Create(launcher, delay: delay, options: FastOptions);
        await session.StartAsync();

        await IntegrationTestHelpers.WaitForStateAsync(session, IntegrationState.Error, TimeSpan.FromSeconds(3));

        Assert.Equal(4, launcher.Starts);
        Assert.Equal(3, delay.Delays.Count);
        Assert.Contains("repeatedly", session.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Too_many_protocol_errors_stop_the_helper()
    {
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);

                for (var id = 2; id <= 7; id++)
                {
                    await IntegrationTestHelpers.SendAsync(helper, id, "eval", new { code = "x" });
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            },
        };
        var log = new RecordingLog();

        await using var session = Create(launcher, log: log);
        await session.StartAsync();

        await IntegrationTestHelpers.WaitForStateAsync(session, IntegrationState.Error, TimeSpan.FromSeconds(3));

        Assert.Contains(log.Lines, line => line.Contains("eval", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_sends_shutdown_and_does_not_restart()
    {
        var shutdown = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeIntegrationLauncher
        {
            Script = async (request, helper) =>
            {
                await IntegrationTestHelpers.InitializeAsync(helper, request);
                var envelope = await IntegrationTestHelpers.ReadAsync(helper);
                shutdown.TrySetResult(
                    envelope?.Kind == JsonRpcKind.Notification
                    && string.Equals(envelope.Method, "shutdown", StringComparison.Ordinal));
            },
        };

        await using var session = Create(launcher);
        await session.StartAsync();
        await session.StopAsync();

        Assert.True(await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(IntegrationState.Stopped, session.State);
        Assert.Equal(1, launcher.Starts);
    }

    private static IntegrationSession Create(
        FakeIntegrationLauncher launcher,
        IReadOnlySet<string>? permissions = null,
        RecordingLog? log = null,
        ImmediateIntegrationDelay? delay = null,
        IntegrationSessionOptions? options = null) =>
        new(
            Manifest,
            "telegram-tdlib",
            @"C:\plugins\telegram-tdlib\helper.exe",
            @"C:\data\integrations\telegram-tdlib",
            permissions ?? Permissions("process", "notifications", "unread", "commands"),
            launcher,
            delay ?? new ImmediateIntegrationDelay(),
            log,
            options ?? FastOptions);

    private static HashSet<string> Permissions(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);
}
