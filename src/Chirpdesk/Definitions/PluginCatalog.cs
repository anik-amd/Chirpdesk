using Chirpdesk.Core.Blocking;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Theming;

namespace Chirpdesk.Definitions;

/// <summary>
/// Builds the catalogue from plugin folders: the signed repository installs into the user folder,
/// and nothing is bundled any more. The disk cleaner is a shell feature, not a plugin.
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

    public static IReadOnlyList<PanelDefinition> LoadPanels(ILog log)
    {
        var panels = PluginLoader.LoadPanels(
            [new PluginScanRoot(AppPaths.BundledPluginsDirectory, Bundled: true)],
            log).ToList();

        if (!panels.Any(panel => string.Equals(panel.Id, DiskCleanerId, StringComparison.Ordinal)))
        {
            panels.Insert(0, DiskCleanerPanel());
        }

        return panels;
    }

    private const string DiskCleanerId = "tool-cleaner";

    /// <summary>
    /// Disk cleaning is part of the shell. It used to be a bundled tool plugin, but the installer
    /// no longer ships content; the panel only names host primitives that the host validates.
    /// </summary>
    private static PanelDefinition DiskCleanerPanel() => new()
    {
        Id = DiskCleanerId,
        Title = "Disk cleaner",
        Subtitle = "Reclaim cache space. Sessions are never touched.",
        Sections =
        [
            new PanelSection
            {
                Type = "text",
                Id = "note",
                Text = "Cookies, Local Storage, IndexedDB and Service Worker are the session and are never trimmed.",
            },
        ],
        Actions =
        [
            new PanelAction
            {
                Id = "trim",
                Label = "Trim caches",
                Primitive = "trimCache",
                Style = "primary",
                Args = new PanelActionArgs { BudgetMb = 60, Targets = "all" },
            },
            new PanelAction
            {
                Id = "scan",
                Label = "Scan for leftovers",
                Primitive = "scanOrphans",
                Style = "secondary",
            },
            new PanelAction
            {
                Id = "reveal",
                Label = "Open profile folder",
                Primitive = "revealFolder",
                Style = "ghost",
                Args = new PanelActionArgs { Root = "profiles" },
            },
        ],
    };

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
