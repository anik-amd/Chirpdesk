namespace Chirpdesk.Core.Time;

/// <summary>The only production implementation. Tests use <c>FakeClock</c>.</summary>
public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
