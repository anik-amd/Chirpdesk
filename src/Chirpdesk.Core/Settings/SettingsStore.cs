using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chirpdesk.Core.Logging;

namespace Chirpdesk.Core.Settings;

/// <summary>
/// Atomic JSON settings with fail-open reads.
/// Writes go to <c>.tmp</c> and are swapped in with <see cref="File.Replace(string, string, string)"/>,
/// so a crash mid-write leaves the previous file intact. A file the app cannot understand is
/// copied to <c>.corrupt</c> and left in place — user data is never destroyed to recover from it.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILog _log;
    private readonly object _gate = new();

    public SettingsStore(string path, ILog log)
    {
        _path = path;
        _log = log;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            string json;
            try
            {
                json = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error("settings: could not read the settings file, using defaults", ex);
                return new AppSettings();
            }

            AppSettings? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                Quarantine("invalid JSON", ex);
                return new AppSettings();
            }

            if (parsed is null)
            {
                Quarantine("empty document", null);
                return new AppSettings();
            }

            if (parsed.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                // Never downgrade or destroy a file written by a newer build.
                _log.Warn($"settings: schemaVersion {parsed.SchemaVersion} is newer than {AppSettings.CurrentSchemaVersion}; using defaults and leaving the file untouched");
                return new AppSettings();
            }

            SettingsValidator.Normalize(parsed);
            var errors = SettingsValidator.Validate(parsed);
            if (errors.Count > 0)
            {
                Quarantine(string.Join("; ", errors), null);
                return new AppSettings();
            }

            return parsed;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            SettingsValidator.Normalize(settings);
            var errors = SettingsValidator.Validate(settings);
            if (errors.Count > 0)
            {
                throw new InvalidSettingsException(string.Join("; ", errors));
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = _path + ".tmp";

            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, _path + ".bak", ignoreMetadataErrors: true);
                TryDelete(_path + ".bak");
            }
            else
            {
                File.Move(temp, _path);
            }
        }
    }

    private void Quarantine(string reason, Exception? exception)
    {
        _log.Warn($"settings: {reason}; falling back to defaults");

        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("settings: could not write the .corrupt copy", ex);
        }

        if (exception is not null)
        {
            _log.Error("settings: parse failure detail", exception);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .bak is harmless; it is never read.
            _log.Warn($"settings: could not delete '{Path.GetFileName(path)}'");
        }
    }
}
