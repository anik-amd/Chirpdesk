using Chirpdesk.Core.Blocking;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Theming;

namespace Chirpdesk.Definitions;

/// <summary>
/// Builds the catalogue from plugin folders: bundled first, then user-installed. A user plugin
/// shadows a bundled one with the same id; one broken folder is logged and skipped.
/// </summary>
internal static class PluginCatalog
{
    public static ServiceCatalog Load(ILog log)
    {
        var result = PluginLoader.LoadServiceDefinitions(
            [
                new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true),
                new PluginScanRoot(AppPaths.UserPluginsDirectory, Bundled: false),
            ],
            log);

        log.Info(
            $"definitions: {result.Definitions.Count} loaded, {result.Errors.Count} skipped, {result.Shadowed.Count} shadowed");

        return new ServiceCatalog(result.Definitions);
    }

    public static IReadOnlyList<PanelDefinition> LoadPanels(ILog log) =>
        PluginLoader.LoadPanels(
            [new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true)],
            log);

    public static IReadOnlyList<InstalledPlugin> LoadInstalled(ILog log) =>
        PluginLoader.LoadInstalledPlugins(
            [
                new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true),
                new PluginScanRoot(AppPaths.UserPluginsDirectory, Bundled: false),
            ],
            log);

    public static IReadOnlyList<FilterPack> LoadFilters(ILog log) =>
        PluginLoader.LoadFilters(
            [
                new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true),
                new PluginScanRoot(AppPaths.UserPluginsDirectory, Bundled: false),
            ],
            log);

    public static IReadOnlyList<ThemePack> LoadThemes(ILog log) =>
        PluginLoader.LoadThemes(
            [
                new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true),
                new PluginScanRoot(AppPaths.UserPluginsDirectory, Bundled: false),
            ],
            log);
}
