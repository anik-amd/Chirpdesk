using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.IO.Pipes;
using Chirpdesk.Core.Integrations;
using Chirpdesk.Core.Logging;
using Chirpdesk.Interop;

namespace Chirpdesk.Integrations;

/// <summary>
/// Windows launcher for integration helpers: a same-user child process, a job object that kills it
/// with the host, and a first-instance named pipe so nothing can squat the name before launch.
/// The helper receives the pipe, token, protocol version and data directory through the
/// environment, never through a command line.
/// </summary>
internal sealed class WindowsIntegrationLauncher(ILog log) : IIntegrationLauncher
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public async Task<IIntegrationProcess> StartAsync(
        IntegrationLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pipe = new NamedPipeServerStream(
            request.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly);

        var job = JobObject.CreateKillOnClose();

        if (job is null)
        {
            log.Warn("integration: could not create a job object; the helper may outlive the app");
        }

        Process? process = null;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = request.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(request.ExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.Environment["CHIRPDESK_PROTOCOL"] =
                request.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment["CHIRPDESK_PIPE"] = request.PipeName;
            startInfo.Environment["CHIRPDESK_TOKEN"] = request.Token;
            startInfo.Environment["CHIRPDESK_DATA"] = request.DataDirectory;

            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("helper process did not start");

            // stdout/stderr are drained to NUL on purpose: a helper that prints a token or a
            // message body must not get it into the host's log.
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);

            if (job is not null && !JobObject.Assign(job, process.Handle))
            {
                log.Warn("integration: could not assign the helper to the job object");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);

            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            return new WindowsIntegrationProcess(process, pipe, job);
        }
        catch
        {
            pipe.Dispose();
            job?.Dispose();

            if (process is not null)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                process.Dispose();
            }

            throw;
        }
    }
}

internal sealed class WindowsIntegrationProcess(Process process, NamedPipeServerStream pipe, SafeJobHandle? job)
    : IIntegrationProcess
{
    private int _disposed;

    public Stream Connection { get; } = pipe;

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return process.HasExited ? process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    public void Kill()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        process.WaitForExitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Kill();
        Connection.Dispose();
        job?.Dispose();
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
