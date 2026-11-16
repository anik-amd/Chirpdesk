namespace Chirpdesk.Hosting;

/// <summary>
/// One process per user session. The mutex is the ownership check; a named auto-reset event
/// is the way a later launch asks the resident instance to show itself. That second path is
/// what makes a toast click work: Windows starts the app through its shortcut, the new process
/// cannot take the mutex, so it signals instead of dying silently.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Chirpdesk.Single";
    private const string ShowEventName = @"Local\Chirpdesk.Show";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly Thread _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _listener = new Thread(Listen) { IsBackground = true, Name = "chirpdesk-show-listener" };
        _listener.Start();
    }

    /// <summary>Raised on the listener thread; the handler must marshal to the UI thread.</summary>
    public event EventHandler? ShowRequested;

    public static bool TryAcquire(out SingleInstance? instance)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            instance = null;
            return false;
        }

        var showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ShowEventName);
        instance = new SingleInstance(mutex, showEvent);
        return true;
    }

    /// <summary>Called by a second launch. False means no resident instance answered.</summary>
    public static bool SignalShow()
    {
        if (!EventWaitHandle.TryOpenExisting(ShowEventName, out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Wake the listener so it can leave its wait, then let it finish before the handle goes.
        _showEvent.Set();
        _ = _listener.Join(TimeSpan.FromMilliseconds(500));

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Another thread released it; nothing to do.
        }

        _mutex.Dispose();
        _showEvent.Dispose();
    }

    private void Listen()
    {
        while (!_disposed)
        {
            try
            {
                if (!_showEvent.WaitOne(TimeSpan.FromMilliseconds(250)))
                {
                    continue;
                }

                if (_disposed)
                {
                    return;
                }

                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}
