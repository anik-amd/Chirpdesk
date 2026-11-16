using Chirpdesk.Core.Time;

namespace Chirpdesk.Core.Tests.Time;

/// <summary>Deterministic clock: every time-dependent decision is testable without waiting.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock() : this(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public FakeClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
