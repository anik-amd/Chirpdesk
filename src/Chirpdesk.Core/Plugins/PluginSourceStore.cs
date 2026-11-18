using System.Text.Json;

namespace Chirpdesk.Core.Plugins;

/// <summary>Where a user plugin came from; written beside plugin.json at install time.</summary>
public sealed record PluginSource(
    string RepositoryUrl,
    string KeyFingerprint,
    string Version,
    string Sha256,
    DateTimeOffset InstalledAt,
    string? ExecutableSha256 = null);

/// <summary>
/// A small sidecar file, never part of the manifest schema. It lets the Plugins page show the
/// source repository, detect a newer signed version and report where an uninstall came from.
/// A missing or corrupt sidecar is not fatal; the plugin is simply shown as locally installed.
/// </summary>
public static class PluginSourceStore
{
    public const string FileName = ".chirpdesk-source.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static PluginSource? TryRead(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PluginSource>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool TryWrite(string folder, PluginSource source)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(source, Options));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
