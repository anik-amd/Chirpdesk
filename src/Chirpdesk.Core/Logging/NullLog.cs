namespace Chirpdesk.Core.Logging;

/// <summary>For tests and for code paths where a log would be noise.</summary>
public sealed class NullLog : ILog
{
    public static NullLog Instance { get; } = new();

    private NullLog()
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message, Exception? exception = null)
    {
    }
}
