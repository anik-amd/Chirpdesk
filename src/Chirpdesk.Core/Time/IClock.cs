namespace Chirpdesk.Core.Time;

/// <summary>
/// Every time-dependent decision (dedupe windows, warm-up, trim schedule) reads the clock
/// through this seam so it can be tested without waiting.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
