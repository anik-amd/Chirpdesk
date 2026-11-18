namespace Chirpdesk.Core.Plugins;

/// <summary>What the running app can install from a signed repository entry.</summary>
public static class RepositoryCompatibility
{
    public const int SupportedApiVersion = 1;

    public static bool IsCompatible(
        RepositoryPlugin plugin,
        Version appVersion,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(appVersion);

        reason = string.Empty;

        if (plugin.ApiVersion != SupportedApiVersion)
        {
            reason = $"plugin API {plugin.ApiVersion} is not supported";
            return false;
        }

        if (plugin.MinAppVersion is not null
            && Version.TryParse(plugin.MinAppVersion, out var minimum)
            && Normalize(appVersion) < Normalize(minimum))
        {
            reason = $"requires Chirpdesk {minimum} or newer";
            return false;
        }

        return true;
    }

    private static Version Normalize(Version value) =>
        new(value.Major, value.Minor, Math.Max(value.Build, 0), Math.Max(value.Revision, 0));
}
