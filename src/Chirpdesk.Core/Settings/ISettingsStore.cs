namespace Chirpdesk.Core.Settings;

/// <summary>The UI never touches disk directly; everything goes through this seam.</summary>
public interface ISettingsStore
{
    /// <summary>Never throws: a missing, corrupt or foreign file yields defaults plus a log line.</summary>
    AppSettings Load();

    /// <summary>Validates, then writes atomically. Throws <see cref="InvalidSettingsException"/> on bad data.</summary>
    void Save(AppSettings settings);
}
