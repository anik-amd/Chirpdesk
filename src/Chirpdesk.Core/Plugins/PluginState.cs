using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Plugins;

/// <summary>
/// Activation state for non-service plugins. Bundled plugins are active by default, so an
/// upgrade that adds one works without a settings migration; only an explicit deactivation is
/// stored. Invalid ids are dropped by the settings normalizer (invariant 8).
/// </summary>
public static class PluginState
{
    /// <summary>The bundled theme plugin that owns the page-darkening master switch.</summary>
    public const string PageDarkId = "page-dark";

    /// <summary>The bundled palette plugin.</summary>
    public const string CatppuccinId = "catppuccin";

    public static bool IsEnabled(AppSettings settings, string pluginId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.DisabledPlugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);
    }

    public static void SetEnabled(AppSettings settings, string pluginId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.DisabledPlugins ??= [];

        var id = pluginId.Trim().ToLowerInvariant();
        settings.DisabledPlugins.RemoveAll(
            existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase));

        if (!enabled)
        {
            settings.DisabledPlugins.Add(id);
        }
    }
}
