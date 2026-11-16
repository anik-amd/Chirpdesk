using Chirpdesk.Core.Time;

namespace Chirpdesk.Core.Notifications;

/// <summary>
/// R4: no toast storm at login. A service must be ready <em>and</em> its title must have
/// stopped changing for <c>stableFor</c> before its notifications are allowed through.
/// Once open the gate stays open for the life of the session: a later title change is a
/// new message, not a warm-up.
/// </summary>
public sealed class WarmUpGate
{
    private readonly IClock _clock;
    private readonly TimeSpan _stableFor;
    private DateTimeOffset _lastChange;
    private int? _lastCount;
    private bool _ready;
    private bool _open;

    public WarmUpGate(IClock clock, TimeSpan stableFor)
    {
        _clock = clock;
        _stableFor = stableFor;
        _lastChange = clock.UtcNow;
    }

    public bool IsOpen => _open;

    public void MarkReady() => _ready = true;

    public void ObserveTitle(int count)
    {
        if (_lastCount == count)
        {
            return;
        }

        _lastCount = count;
        _lastChange = _clock.UtcNow;
    }

    public bool ShouldSuppress()
    {
        if (_open)
        {
            return false;
        }

        if (!_ready || _clock.UtcNow - _lastChange < _stableFor)
        {
            return true;
        }

        _open = true;
        return false;
    }
}
