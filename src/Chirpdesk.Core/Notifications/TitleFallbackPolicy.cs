namespace Chirpdesk.Core.Notifications;

/// <summary>
/// The decision behind the title-only fallback: a generic toast is worth showing only when the
/// gate is open, the title has been quiet long enough to rule out a reconnect replay, and no
/// other open service already covers the same conversation (Facebook's fallback yields to
/// Messenger's).
/// </summary>
public static class TitleFallbackPolicy
{
    /// <summary>Long enough to tell an arrival from a backlog burst, short enough to feel prompt.</summary>
    public static readonly TimeSpan RequiredQuiet = TimeSpan.FromSeconds(15);

    public static bool ShouldStart(bool gateOpen, TimeSpan quietFor, bool coveredElsewhere) =>
        gateOpen && quietFor >= RequiredQuiet && !coveredElsewhere;
}
