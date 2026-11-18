namespace Chirpdesk.Core.Integrations;

public enum IntegrationState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Error,
}

public sealed record IntegrationStateChanged(IntegrationState State, string? Detail);

public sealed record IntegrationCommandResult(bool Ok, string? Message, string? Error)
{
    public static IntegrationCommandResult Failure(string error) => new(false, null, error);
}

public sealed record IntegrationLaunchRequest(
    string PluginId,
    string ExecutablePath,
    string DataDirectory,
    string PipeName,
    string Token,
    int ProtocolVersion);

/// <summary>
/// One launched helper. The host owns the process and its job object; the stream is the connected
/// pipe. Tests supply an in-memory implementation with the same shape.
/// </summary>
public interface IIntegrationProcess : IAsyncDisposable
{
    Stream Connection { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    void Kill();

    Task WaitForExitAsync(CancellationToken cancellationToken);
}

/// <summary>Starts a helper and returns a connected process. Windows implementation owns the pipe.</summary>
public interface IIntegrationLauncher
{
    Task<IIntegrationProcess> StartAsync(IntegrationLaunchRequest request, CancellationToken cancellationToken);
}

/// <summary>Injectable delay so restart backoff is testable without real waits.</summary>
public interface IIntegrationDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemIntegrationDelay : IIntegrationDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
