using Chirpdesk.Core.Time;

namespace Chirpdesk.Core.Notifications;

/// <summary>
/// R4: no toast storm at login. A service's notifications stay suppressed for
/// <c>stableFor</c> after its page reports ready, so a reconnecting session cannot replay a
/// backlog into the Action Center.
/// <para>
/// The window is measured from readiness, not from the last title change. An earlier version
/// waited for a stable title, which deadlocked in practice: an arriving message changes the
/// unread title at the same moment it notifies, so the gate was re-armed by the very event it
/// was supposed to let through and WhatsApp toasts never appeared. Replay suppression is the
/// boot cutoff's job (it compares the notification's own timestamp); this gate only covers
/// the first seconds of a session.
/// </para>
/// </summary>
public sealed class WarmUpGate
{
    private readonly IClock _clock;
    private readonly TimeSpan _stableFor;
    private DateTimeOffset? _readyAt;
    private bool _open;

    public WarmUpGate(IClock clock, TimeSpan stableFor)
    {
        _clock = clock;
        _stableFor = stableFor;
    }

    public bool IsOpen => _open;

    /// <summary>Idempotent: the first readiness wins, so a later navigation cannot extend it.</summary>
    public void MarkReady() => _readyAt ??= _clock.UtcNow;

    /// <summary>
    /// Records the current unread count for the title-only fallback. Deliberately does not
    /// affect suppression; see the type comment for why.
    /// </summary>
    public void ObserveTitle(int count) => LastCount = count;

    /// <summary>The most recent unread count seen in the title, or null before the first one.</summary>
    public int? LastCount { get; private set; }

    public bool ShouldSuppress()
    {
        if (_open)
        {
            return false;
        }

        if (_readyAt is null || _clock.UtcNow - _readyAt.Value < _stableFor)
        {
            return true;
        }

        _open = true;
        return false;
    }
}
