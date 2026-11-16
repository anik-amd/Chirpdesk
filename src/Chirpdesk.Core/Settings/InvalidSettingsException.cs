namespace Chirpdesk.Core.Settings;

/// <summary>
/// Thrown when code tries to <em>write</em> settings that violate the schema. Reading bad data
/// never throws — that path fails open with defaults (see <see cref="ISettingsStore.Load"/>).
/// </summary>
public sealed class InvalidSettingsException : Exception
{
    public InvalidSettingsException(string message) : base(message)
    {
    }

    public InvalidSettingsException(string message, Exception inner) : base(message, inner)
    {
    }
}
