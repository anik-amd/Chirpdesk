using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Integrations;

/// <summary>
/// Reads `integration.json` and checks the executable it names. The host installs and launches
/// only through this result: a missing or oversized helper is a plugin error, never a crash.
/// </summary>
public static class IntegrationLoader
{
    public static IntegrationManifest? Read(string folder, out IReadOnlyList<string> errors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var problems = new List<string>();
        var path = Path.Combine(folder, "integration.json");

        if (!File.Exists(path))
        {
            errors = ["missing integration.json"];
            return null;
        }

        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors = [$"cannot read integration.json: {ex.Message}"];
            return null;
        }

        var manifest = IntegrationManifestCodec.TryRead(json, out var error);

        if (manifest is null)
        {
            errors = [error ?? "invalid integration.json"];
            return null;
        }

        var executable = Path.Combine(folder, manifest.Executable);

        if (!File.Exists(executable))
        {
            problems.Add($"executable '{manifest.Executable}' is missing");
        }
        else
        {
            var length = new FileInfo(executable).Length;

            if (length == 0)
            {
                problems.Add($"executable '{manifest.Executable}' is empty");
            }
            else if (length > PluginArchivePolicy.Integration.MaxBytes)
            {
                problems.Add($"executable '{manifest.Executable}' exceeds the integration size limit");
            }
        }

        errors = problems;
        return problems.Count == 0 ? manifest : null;
    }
}
