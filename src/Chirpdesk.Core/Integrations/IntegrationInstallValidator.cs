using System.Security.Cryptography;
using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Integrations;

/// <summary>
/// Install-time gate for an extracted integration: the folder must carry a valid
/// `integration.json` and its declared executable, and the executable's SHA-256 is pinned for the
/// launch-time re-check.
/// </summary>
public static class IntegrationInstallValidator
{
    public static bool TryValidate(
        string folder,
        RepositoryPlugin entry,
        out string executableSha256,
        out string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(entry);
        executableSha256 = string.Empty;
        error = string.Empty;

        if (!string.Equals(entry.Kind, PluginLoader.IntegrationKind, StringComparison.Ordinal))
        {
            error = "not an integration";
            return false;
        }

        var manifest = IntegrationLoader.Read(folder, out var errors);

        if (manifest is null)
        {
            error = string.Join("; ", errors);
            return false;
        }

        var executable = Path.Combine(folder, manifest.Executable);

        try
        {
            using var stream = File.OpenRead(executable);
            executableSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"could not hash the helper executable: {ex.Message}";
            return false;
        }
    }
}
