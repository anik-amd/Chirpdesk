namespace Chirpdesk.Hosting;

/// <summary>
/// One process per user session. The mutex is the whole mechanism in phase 0; phase 1 adds a
/// named pipe so a second launch can hand its request to the running instance instead of dying.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Chirpdesk.Single";

    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static bool TryAcquire(out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        instance = new SingleInstance(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Another thread released it; nothing to do.
        }

        _mutex.Dispose();
    }
}
