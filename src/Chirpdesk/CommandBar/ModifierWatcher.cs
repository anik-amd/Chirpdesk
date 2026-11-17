using System.Windows.Threading;
using Chirpdesk.Interop;

namespace Chirpdesk.CommandBar;

/// <summary>
/// Polls the Ctrl key while the bar is open, because a WebView2 page never delivers the
/// modifier key-up that would otherwise commit the cycle (ADR 0002). The impurity is here and
/// nowhere else.
/// </summary>
internal sealed class ModifierWatcher
{
    private readonly DispatcherTimer _timer;

    public ModifierWatcher(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(30),
            DispatcherPriority.Input,
            OnTick,
            dispatcher);
        _timer.Stop();
    }

    public event EventHandler? ControlReleased;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>Stops polling and drops the subscriber; used when the app exits.</summary>
    public void Detach()
    {
        Stop();
        ControlReleased = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (NativeMethods.IsControlDown())
        {
            return;
        }

        Stop();
        ControlReleased?.Invoke(this, EventArgs.Empty);
    }
}
