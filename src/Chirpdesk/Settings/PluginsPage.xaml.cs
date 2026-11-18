using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Integrations;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Storage;
using Chirpdesk.Core.Settings;
using Chirpdesk.Hosting;
using Chirpdesk.Integrations;
using Chirpdesk.Repository;
using Chirpdesk.Storage;
using Chirpdesk.Ui;

namespace Chirpdesk.Settings;

/// <summary>
/// The plugin list that replaces the old "add service" form. Turning a service plugin on
/// creates its first account; adding an account creates another instance of the same plugin.
/// Turning it off disables the instances and sleeps their runtimes; profiles stay.
/// </summary>
public partial class PluginsPage : UserControl
{
    private readonly ServiceCatalog _catalog;
    private readonly IReadOnlyList<InstalledPlugin> _inventory;
    private readonly SettingsStore _store;
    private readonly ServiceRegistry _registry;
    private readonly ILog _log;
    private readonly Func<ServiceDefinition, Task> _enable;
    private readonly Func<ServiceDefinition, Task> _addAccount;
    private readonly Func<ServiceDefinition, Task> _disable;
    private readonly Func<string, bool, Task> _setPluginEnabled;
    private readonly Action _back;
    private readonly RepositoryService _repositories;
    private readonly Func<string, Task<bool>> _uninstallPlugin;
    private readonly Action _reloadPlugins;
    private readonly IntegrationRuntimeHost? _integrations;
    private readonly Func<string, Task> _removeInstance;
    private List<OrphanItem> _cleanerOrphans = [];
    private bool _cleanerScanned;
    private readonly HashSet<string> _removedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RepositoryIndex> _synced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Pem, string Fingerprint)> _keyChanges = new(StringComparer.OrdinalIgnoreCase);
    private RepositorySetting? _editingRepository;
    private (RepositorySetting Repository, RepositoryIndex Index, RepositoryPlugin Plugin)? _pendingInstall;
    private bool _integrationConsentConfirmed;
    private AppSettings _settings;

    internal PluginsPage(
        ServiceCatalog catalog,
        IReadOnlyList<InstalledPlugin> inventory,
        SettingsStore store,
        ServiceRegistry registry,
        ILog log,
        Func<ServiceDefinition, Task> enable,
        Func<ServiceDefinition, Task> addAccount,
        Func<ServiceDefinition, Task> disable,
        Func<string, bool, Task> setPluginEnabled,
        Action back,
        RepositoryService repositories,
        Func<string, Task<bool>> uninstallPlugin,
        Action reloadPlugins,
        IntegrationRuntimeHost? integrations = null,
        Func<string, Task>? removeInstance = null)
    {
        InitializeComponent();
        _catalog = catalog;
        _inventory = inventory;
        _store = store;
        _registry = registry;
        _log = log;
        _enable = enable;
        _addAccount = addAccount;
        _disable = disable;
        _setPluginEnabled = setPluginEnabled;
        _back = back;
        _repositories = repositories;
        _uninstallPlugin = uninstallPlugin;
        _reloadPlugins = reloadPlugins;
        _integrations = integrations;
        _removeInstance = removeInstance ?? (_ => Task.CompletedTask);
        _settings = store.Load();
        PageIcon.Content = UiIcons.Create("plugins", Token("CdAccent"), 28);
        Reload();
    }

    public void Reload()
    {
        _settings = _store.Load();
        RenderPluginCards();
        RenderAvailablePlugins();

        // Read every repository once per page load: the 4 h cache makes it free offline, and
        // without an index an installed card cannot know that a newer signed version exists.
        foreach (var repository in _settings.Repositories
            .Where(item => !_synced.ContainsKey(item.Url)
                || (item.AutoUpdate
                    && (item.LastSync is null || DateTimeOffset.UtcNow - item.LastSync > RepositoryService.CacheTtl)))
            .ToList())
        {
            _ = SyncRepositoryAsync(repository, trustNewKey: true);
        }
    }

    /// <summary>Called by the runtime when a helper changes state while the page is open.</summary>
    public void RefreshIntegrationStatus() => Reload();

    private void OnShowInstalled(object sender, RoutedEventArgs e)
    {
        if (InstalledTab is null || BrowseTab is null)
        {
            return;
        }

        InstalledTab.Visibility = Visibility.Visible;
        BrowseTab.Visibility = Visibility.Collapsed;
    }

    private void OnShowBrowse(object sender, RoutedEventArgs e)
    {
        if (InstalledTab is null || BrowseTab is null)
        {
            return;
        }

        InstalledTab.Visibility = Visibility.Collapsed;
        BrowseTab.Visibility = Visibility.Visible;
        RenderAvailablePlugins();
    }

    /// <summary>
    /// Shows a repository name beside an install path. A source record is written only by the
    /// repository installer, so a null source means the plugin came with the app or the user
    /// dropped it into the plugins folder.
    /// </summary>
    private string SourceLabel(InstalledPlugin? plugin)
    {
        if (plugin?.Source is { } source)
        {
            var repository = _settings.Repositories.FirstOrDefault(item =>
                string.Equals(item.Url, source.RepositoryUrl, StringComparison.OrdinalIgnoreCase));
            var name = repository?.Name;

            return string.IsNullOrWhiteSpace(name)
                ? source.RepositoryUrl
                : $"{name} · {source.RepositoryUrl}";
        }

        return plugin?.Bundled == true ? "Bundled with Chirpdesk" : "Installed locally";
    }

    /// <summary>
    /// Rebuilds the installed-plugin cards. A sync refreshes the available list on its own, but
    /// the cards carry their own update and rollback buttons, so a new index has to rebuild them.
    /// </summary>
    private void RenderPluginCards()
    {
        PluginsPanel.Children.Clear();

        foreach (var definition in _catalog.Definitions.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var instances = _settings.Services
                .Where(instance => string.Equals(instance.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var enabled = instances.Any(instance => instance.Enabled);
            var active = instances.Count(instance => instance.Enabled);
            var installedPlugin = _inventory.FirstOrDefault(item =>
                string.Equals(item.Id, definition.Id, StringComparison.Ordinal));

            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 320,
                Margin = new Thickness(0, 0, 14, 14),
                Padding = new Thickness(14),
            };

            var content = new StackPanel();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var badge = ServiceIcons.Create(definition.Id, definition.Name, definition.Accent, 34);
            badge.Opacity = enabled ? 1 : 0.45;
            Grid.SetColumn(badge, 0);
            header.Children.Add(badge);

            var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            title.Children.Add(new TextBlock
            {
                Text = definition.Name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
            });
            title.Children.Add(new TextBlock
            {
                Text = enabled ? $"Active · {active} account(s)" : "Inactive",
                FontSize = 11,
                Foreground = enabled ? Token("CdAccent") : Token("CdTextMuted"),
            });
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            content.Children.Add(header);

            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(definition.Description)
                    ? "Activate to add this service; add another account for a second login."
                    : definition.Description,
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                MinHeight = 34,
            });

            var serviceVersion = installedPlugin?.Version;

            if (installedPlugin is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(serviceVersion)
                        ? SourceLabel(installedPlugin)
                        : $"v{serviceVersion} · {SourceLabel(installedPlugin)}",
                    FontSize = 10,
                    Foreground = Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            var actions = new WrapPanel();
            var primary = new Button
            {
                Content = enabled ? "Add account" : "Activate",
                Width = 104,
                Margin = new Thickness(0, 0, 6, 6),
                Style = (Style)FindResource("PillButton"),
            };
            primary.Click += async (_, _) => await Run(enabled ? _addAccount : _enable, definition);
            actions.Children.Add(primary);

            var off = new Button
            {
                Content = "Deactivate",
                Width = 86,
                IsEnabled = enabled,
                Style = (Style)FindResource("GhostButton"),
                Margin = new Thickness(0, 0, 6, 6),
            };
            off.Click += async (_, _) => await Run(_disable, definition);
            actions.Children.Add(off);

            var serviceSettings = new Button
            {
                Content = "Settings",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
            };
            serviceSettings.Click += (_, _) => OpenServiceSettings(definition.Id, definition.Name);
            actions.Children.Add(serviceSettings);

            if (installedPlugin is { } installedService)
            {
                var serviceUpdate = FindUpdate(installedService);
                if (serviceUpdate is { } candidate)
                {
                    var updateButton = new Button
                    {
                        Content = $"Update to {candidate.Plugin.Version}",
                        Style = (Style)FindResource("PillButton"),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 6, 6),
                    };
                    updateButton.Click += (_, _) =>
                        OpenPluginDetails(candidate.Repository, candidate.Index, candidate.Plugin);
                    actions.Children.Add(updateButton);
                }

                if (!installedService.Bundled && RepositoryService.HasBackup(installedService.Id))
                {
                    var rollback = new Button
                    {
                        Content = "Roll back",
                        Style = (Style)FindResource("GhostButton"),
                        Padding = new Thickness(10, 5, 10, 5),
                        Margin = new Thickness(0, 0, 6, 6),
                    };
                    rollback.Click += async (_, _) =>
                    {
                        await StopIntegrationAsync(installedService.Id);
                        var result = await _repositories.RollbackAsync(installedService.Id);
                        RepositoryStatus.Text = result.Message;
                        if (result.Ok)
                        {
                            _reloadPlugins();
                        }
                    };
                    actions.Children.Add(rollback);
                }

                var uninstall = new Button
                {
                    Content = installedService.Bundled ? "Remove" : "Uninstall",
                    Style = (Style)FindResource("GhostButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                uninstall.Click += async (_, _) =>
                {
                    if (await _uninstallPlugin(installedService.Id))
                    {
                        _removedPlugins.Add(installedService.Id);
                        _reloadPlugins();
                    }
                };
                actions.Children.Add(uninstall);
            }

            content.Children.Add(actions);
            card.Child = content;
            PluginsPanel.Children.Add(card);
        }

        foreach (var plugin in _inventory.Where(item => !string.Equals(item.Kind, PluginLoader.ServiceKind, StringComparison.Ordinal) && !_removedPlugins.Contains(item.Id)))
        {
            var toggleable = plugin.Kind is "theme" or "filter" or PluginLoader.IntegrationKind;
            var pluginEnabled = !toggleable || PluginState.IsEnabled(_settings, plugin.Id);

            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 320,
                Margin = new Thickness(0, 0, 14, 14),
                Padding = new Thickness(14),
            };

            var content = new StackPanel();

            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            var icon = string.Equals(plugin.Id, PluginState.PageDarkId, StringComparison.OrdinalIgnoreCase)
                ? UiIcons.Create("moon", Token("CdAccent"), 28)
                : UiIcons.Create("plugins", Token("CdAccent"), 28);
            icon.Opacity = pluginEnabled ? 1 : 0.45;
            header.Children.Add(icon);

            var title = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            title.Children.Add(new TextBlock
            {
                Text = plugin.Name,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
            });
            title.Children.Add(new TextBlock
            {
                Text = toggleable
                    ? pluginEnabled ? $"Active · {plugin.Kind} plugin" : "Inactive"
                    : "Active · bundled tool",
                FontSize = 11,
                Foreground = pluginEnabled ? Token("CdAccent") : Token("CdTextMuted"),
            });
            header.Children.Add(title);
            content.Children.Add(header);

            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(plugin.Description)
                    ? "Installed plugin."
                    : plugin.Description,
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 34,
                Margin = toggleable ? new Thickness(0, 0, 0, 12) : new Thickness(0),
            });

            if (!string.IsNullOrWhiteSpace(plugin.Version) || plugin.Source is not null || plugin.Bundled)
            {
                content.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(plugin.Version)
                        ? SourceLabel(plugin)
                        : $"v{plugin.Version} · {SourceLabel(plugin)}",
                    FontSize = 10,
                    Foreground = Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            var integrationStatus = string.Equals(plugin.Kind, PluginLoader.IntegrationKind, StringComparison.Ordinal)
                ? _integrations?.StatusOf(plugin.Id)
                : null;

            if (string.Equals(plugin.Kind, PluginLoader.IntegrationKind, StringComparison.Ordinal))
            {
                var statusText = integrationStatus is null
                    ? "Helper not running."
                    : integrationStatus.State switch
                    {
                        IntegrationState.Running => "Helper running.",
                        IntegrationState.Starting => "Helper starting.",
                        IntegrationState.Stopping => "Helper stopping.",
                        IntegrationState.Error => $"Helper error: {integrationStatus.Error}",
                        _ => "Helper stopped.",
                    };

                if (integrationStatus?.HelperStatus is { Length: > 0 } helperStatus
                    && integrationStatus.State == IntegrationState.Running)
                {
                    statusText += $" {helperStatus}";
                }

                content.Children.Add(new TextBlock
                {
                    Text = statusText,
                    FontSize = 11,
                    Foreground = integrationStatus?.State == IntegrationState.Error
                        ? Token("CdText")
                        : Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                });

                var pluginFolder = Path.Combine(
                    plugin.Bundled ? AppPaths.BundledPluginsDirectory : AppPaths.UserPluginsDirectory,
                    plugin.Id);
                var manifest = PluginLoader.ReadInstalledManifest(pluginFolder, out _);
                var permissions = manifest?.Permissions is { Count: > 0 } declared
                    ? string.Join(", ", declared)
                    : "none";

                content.Children.Add(new TextBlock
                {
                    Text = $"Host permissions: {permissions}",
                    FontSize = 10,
                    Foreground = Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                });
            }

            var actions = new WrapPanel();

            if (toggleable)
            {
                var toggle = new Button
                {
                    Content = pluginEnabled ? "Deactivate" : "Activate",
                    Style = (Style)FindResource(pluginEnabled ? "GhostButton" : "PillButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                toggle.Click += async (_, _) => await RunPlugin(plugin.Id, !pluginEnabled);
                actions.Children.Add(toggle);
            }

            if (string.Equals(plugin.Kind, PluginLoader.IntegrationKind, StringComparison.Ordinal)
                && _integrations is not null)
            {
                var state = integrationStatus?.State;

                var start = new Button
                {
                    Content = "Start",
                    IsEnabled = state is null or IntegrationState.Stopped or IntegrationState.Error,
                    Style = (Style)FindResource("PillButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                start.Click += async (_, _) => await RunIntegrationAsync(() => _integrations.StartAsync(plugin.Id));
                actions.Children.Add(start);

                var stop = new Button
                {
                    Content = "Stop",
                    IsEnabled = state is IntegrationState.Running or IntegrationState.Starting,
                    Style = (Style)FindResource("GhostButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                stop.Click += async (_, _) => await RunIntegrationAsync(() => _integrations.StopAsync(plugin.Id));
                actions.Children.Add(stop);

                var restart = new Button
                {
                    Content = "Restart",
                    IsEnabled = state is IntegrationState.Running or IntegrationState.Error,
                    Style = (Style)FindResource("GhostButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                restart.Click += async (_, _) => await RunIntegrationAsync(async () =>
                {
                    await _integrations.StopAsync(plugin.Id);
                    await _integrations.StartAsync(plugin.Id);
                });
                actions.Children.Add(restart);

                var dataFolder = new Button
                {
                    Content = "Data folder",
                    Style = (Style)FindResource("GhostButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                dataFolder.Click += (_, _) =>
                    OpenFolder(Path.Combine(AppPaths.DataRoot, "integrations", plugin.Id));
                actions.Children.Add(dataFolder);
            }

            var pluginSettings = new Button
            {
                Content = "Settings",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
            };
            pluginSettings.Click += (_, _) => OpenPluginSettings(plugin);
            actions.Children.Add(pluginSettings);

            var update = FindUpdate(plugin);
            if (update is { } candidate)
            {
                var updateButton = new Button
                {
                    Content = $"Update to {candidate.Plugin.Version}",
                    Style = (Style)FindResource("PillButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                updateButton.Click += (_, _) =>
                    OpenPluginDetails(candidate.Repository, candidate.Index, candidate.Plugin);
                actions.Children.Add(updateButton);
            }

            if (!plugin.Bundled && RepositoryService.HasBackup(plugin.Id))
            {
                var rollback = new Button
                {
                    Content = "Roll back",
                    Style = (Style)FindResource("GhostButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                rollback.Click += async (_, _) =>
                {
                    await StopIntegrationAsync(plugin.Id);
                    var result = await _repositories.RollbackAsync(plugin.Id);
                    RepositoryStatus.Text = result.Message;
                    if (result.Ok)
                    {
                        _reloadPlugins();
                    }
                };
                actions.Children.Add(rollback);
            }

            var uninstall = new Button
            {
                Content = plugin.Bundled ? "Remove" : "Uninstall",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 6),
            };
            uninstall.Click += async (_, _) =>
            {
                await StopIntegrationAsync(plugin.Id);

                if (await _uninstallPlugin(plugin.Id))
                {
                    _removedPlugins.Add(plugin.Id);
                    _reloadPlugins();
                }
            };
            actions.Children.Add(uninstall);

            if (actions.Children.Count > 0)
            {
                content.Children.Add(actions);
            }

            card.Child = content;
            PluginsPanel.Children.Add(card);
        }

        InstalledSummary.Text = PluginsPanel.Children.Count == 0
            ? "No plugins installed yet. Browse a repository or drop a plugin into the plugins folder."
            : $"{PluginsPanel.Children.Count} plugin(s) installed · activate or deactivate without losing profiles.";
    }

    private (RepositorySetting Repository, RepositoryIndex Index, RepositoryPlugin Plugin)? FindUpdate(InstalledPlugin installed)
    {
        if (installed.Source is null
            || !Version.TryParse(installed.Version, out var current))
        {
            return null;
        }

        foreach (var repository in _settings.Repositories)
        {
            if (!string.Equals(repository.Url, installed.Source.RepositoryUrl, StringComparison.OrdinalIgnoreCase)
                || !_synced.TryGetValue(repository.Url, out var index))
            {
                continue;
            }

            var candidate = index.Plugins.FirstOrDefault(item =>
                string.Equals(item.Id, installed.Id, StringComparison.Ordinal)
                && Version.TryParse(item.Version, out var available)
                && available > current);

            if (candidate is not null)
            {
                return (repository, index, candidate);
            }
        }

        return null;
    }

    private async Task Run(Func<ServiceDefinition, Task> action, ServiceDefinition definition)
    {
        try
        {
            await action(definition);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException or ArgumentException)
        {
            _log.Error($"plugins page: action failed for '{definition.Id}'", ex);
            MessageBox.Show(ex.Message, "Plugins", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RunPlugin(string pluginId, bool enabled)
    {
        try
        {
            await _setPluginEnabled(pluginId, enabled);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException or ArgumentException)
        {
            _log.Error($"plugins page: could not toggle '{pluginId}'", ex);
            MessageBox.Show(ex.Message, "Plugins", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RenderAvailablePlugins()
    {
        AvailablePluginsPanel.Children.Clear();

        if (_settings.Repositories.Count == 0)
        {
            RepositoryStatus.Text = "No repositories configured. Add a repository to browse its plugins.";
            return;
        }

        var rendered = 0;
        var app = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

        foreach (var repository in _settings.Repositories)
        {
            if (!_synced.TryGetValue(repository.Url, out var index))
            {
                continue;
            }

            rendered++;
            var repositoryName = string.IsNullOrWhiteSpace(repository.Name) ? repository.Url : repository.Name;

            AvailablePluginsPanel.Children.Add(new TextBlock
            {
                Text = repositoryName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
                Margin = new Thickness(0, rendered == 1 ? 0 : 18, 0, 1),
            });
            AvailablePluginsPanel.Children.Add(new TextBlock
            {
                Text = repository.Url,
                FontSize = 10,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var grid = new WrapPanel { Orientation = Orientation.Horizontal };

            foreach (var plugin in index.Plugins)
            {
                grid.Children.Add(BuildBrowseCard(repository, index, plugin, app, repositoryName));
            }

            AvailablePluginsPanel.Children.Add(grid);
        }

        RepositoryStatus.Text = rendered == 0
            ? $"{_settings.Repositories.Count} repository(ies) configured. Sync one to see plugins."
            : $"Showing plugins from {rendered} synced repository(ies). Select a plugin to inspect it.";
    }

    private Border BuildBrowseCard(
        RepositorySetting repository,
        RepositoryIndex index,
        RepositoryPlugin plugin,
        Version app,
        string repositoryName)
    {
        var compatible = RepositoryCompatibility.IsCompatible(plugin, app, out var reason);
        var installed = _inventory.FirstOrDefault(item =>
            string.Equals(item.Id, plugin.Id, StringComparison.Ordinal));
        var installedVersion = installed?.Version;
        var upToDate = string.Equals(installedVersion, plugin.Version, StringComparison.Ordinal);
        var updateAvailable = installedVersion is not null
            && Version.TryParse(installedVersion, out var current)
            && Version.TryParse(plugin.Version, out var available)
            && available > current;

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Width = 250,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        card.MouseLeftButtonUp += (_, _) => OpenPluginDetails(repository, index, plugin);

        var content = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var icon = plugin.Kind switch
        {
            PluginLoader.ServiceKind => ServiceIcons.Create(plugin.Id, plugin.Name, null, 32),
            "theme" => UiIcons.Create("moon", Token("CdAccent"), 30),
            "filter" => UiIcons.Create("shield", Token("CdAccent"), 30),
            _ => UiIcons.Create("plugins", Token("CdAccent"), 30),
        };
        icon.Opacity = compatible ? 1 : 0.45;
        header.Children.Add(icon);

        var title = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = plugin.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("CdText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        title.Children.Add(new TextBlock
        {
            Text = $"v{plugin.Version} · {plugin.Kind}",
            FontSize = 10,
            Foreground = Token("CdTextMuted"),
        });
        header.Children.Add(title);
        content.Children.Add(header);

        content.Children.Add(new TextBlock
        {
            Text = $"From {repositoryName}",
            FontSize = 10,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        content.Children.Add(new TextBlock
        {
            Text = !compatible
                ? reason
                : upToDate
                    ? "Installed and up to date."
                    : updateAvailable
                        ? $"Installed {installedVersion} · update available."
                        : "Not installed.",
            FontSize = 11,
            Foreground = compatible ? Token("CdAccent") : Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            MinHeight = 28,
        });

        var actions = new WrapPanel();
        var primary = new Button
        {
            Content = upToDate ? "Installed" : updateAvailable ? "Update" : "Install",
            IsEnabled = compatible && !upToDate,
            Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 6, 0),
        };
        primary.Click += (_, _) => OpenPluginDetails(repository, index, plugin);
        actions.Children.Add(primary);

        var details = new Button
        {
            Content = "Details",
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(10, 5, 10, 5),
        };
        details.Click += (_, _) => OpenPluginDetails(repository, index, plugin);
        actions.Children.Add(details);

        content.Children.Add(actions);
        card.Child = content;
        return card;
    }

    private void RenderManageRepositories()
    {
        ManageRepositoriesPanel.Children.Clear();

        if (_settings.Repositories.Count == 0)
        {
            ManageRepositoriesPanel.Children.Add(new TextBlock
            {
                Text = "No repositories yet. Use Add repository in the Browse tab to add one.",
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            });
            return;
        }

        foreach (var repository in _settings.Repositories.ToList())
        {
            var row = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
            };

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(repository.Name) ? repository.Url : repository.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
            });
            content.Children.Add(new TextBlock
            {
                Text = repository.Url,
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(repository.KeyFingerprint)
                    ? "Key not pinned yet."
                    : $"Key {repository.KeyFingerprint}",
                FontSize = 11,
                Foreground = Token("CdAccent"),
                Margin = new Thickness(0, 3, 0, 2),
            });
            content.Children.Add(new TextBlock
            {
                Text = repository.LastSync is { } last
                    ? $"Last sync {last.ToLocalTime():yyyy-MM-dd HH:mm} · cache window 4 h"
                    : "Never synced.",
                FontSize = 10,
                Foreground = Token("CdTextMuted"),
                Margin = new Thickness(0, 0, 0, 6),
            });

            var auto = new CheckBox
            {
                Content = "Auto-update when the 4 h cache is stale",
                IsChecked = repository.AutoUpdate,
                Style = (Style)FindResource("CdSwitch"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            auto.Checked += (_, _) => SetRepositoryAutoUpdate(repository, true);
            auto.Unchecked += (_, _) => SetRepositoryAutoUpdate(repository, false);
            content.Children.Add(auto);

            if (_keyChanges.TryGetValue(repository.Url, out var pendingKey))
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"New key: {pendingKey.Fingerprint}\nOld key: {repository.KeyFingerprint}",
                    FontSize = 11,
                    Foreground = Token("CdAccent"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 6),
                });
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var update = new Button
            {
                Content = "Update",
                Style = (Style)FindResource("PillButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
            };
            update.Click += async (_, _) => await SyncRepositoryAsync(repository, trustNewKey: true);
            actions.Children.Add(update);

            var edit = new Button
            {
                Content = "Edit",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
            };
            edit.Click += (_, _) => OpenEditRepository(repository);
            actions.Children.Add(edit);

            if (_keyChanges.TryGetValue(repository.Url, out var trustKey))
            {
                var trust = new Button
                {
                    Content = "Trust new key",
                    Style = (Style)FindResource("PillButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 0),
                };
                trust.Click += async (_, _) => await TrustRepositoryAsync(repository, trustKey.Pem, trustKey.Fingerprint);
                actions.Children.Add(trust);
            }

            var delete = new Button
            {
                Content = "Delete",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 5, 10, 5),
            };
            delete.Click += (_, _) => RemoveRepository(repository);
            actions.Children.Add(delete);
            content.Children.Add(actions);
            row.Child = content;
            ManageRepositoriesPanel.Children.Add(row);
        }
    }

    private void OnOpenAddRepository(object sender, RoutedEventArgs e)
    {
        _editingRepository = null;
        ModalTitle.Text = "Add plugins repository";
        ModalDescription.Text = "Point at a static HTTPS folder containing index.json, index.json.sig and key.pem. The key is pinned on first sync.";
        ModalRepositoryUrl.Text = string.Empty;
        AddRepositoryForm.Visibility = Visibility.Visible;
        ManageRepositoryList.Visibility = Visibility.Collapsed;
        ModalPrimaryButton.Content = "Add repository";
        ModalPrimaryButton.Visibility = Visibility.Visible;
        ModalLayer.Visibility = Visibility.Visible;
        ModalRepositoryUrl.Focus();
    }

    private void OnOpenManageRepositories(object sender, RoutedEventArgs e)
    {
        _editingRepository = null;
        ModalTitle.Text = "Plugin repositories";
        ModalDescription.Text = "Add, edit, update or remove the static repositories Chirpdesk can install from.";
        AddRepositoryForm.Visibility = Visibility.Collapsed;
        ManageRepositoryList.Visibility = Visibility.Visible;
        ModalPrimaryButton.Content = "Add new";
        ModalPrimaryButton.Visibility = Visibility.Visible;
        ModalSecondaryButton.Content = "Close";
        RenderManageRepositories();
        ModalLayer.Visibility = Visibility.Visible;
    }

    private void OpenEditRepository(RepositorySetting repository)
    {
        _editingRepository = repository;
        ModalTitle.Text = "Edit repository";
        ModalDescription.Text = "Changing the URL fetches a new index and re-pins the key on the next sync.";
        ModalRepositoryUrl.Text = repository.Url;
        AddRepositoryForm.Visibility = Visibility.Visible;
        ManageRepositoryList.Visibility = Visibility.Collapsed;
        ModalPrimaryButton.Content = "Save changes";
        ModalPrimaryButton.Visibility = Visibility.Visible;
        ModalSecondaryButton.Content = "Cancel";
        ModalLayer.Visibility = Visibility.Visible;
        ModalRepositoryUrl.Focus();
    }

    private void OnCloseModal(object sender, RoutedEventArgs e)
    {
        ModalLayer.Visibility = Visibility.Collapsed;
        _editingRepository = null;
    }

    private async void OnSaveRepository(object sender, RoutedEventArgs e)
    {
        if (ModalPrimaryButton.Content as string == "Add new")
        {
            OnOpenAddRepository(sender, e);
            return;
        }

        var url = ModalRepositoryUrl.Text.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            RepositoryStatus.Text = "Enter an absolute https repository URL.";
            return;
        }

        if (_editingRepository is null
            && _settings.Repositories.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            RepositoryStatus.Text = "That repository is already added.";
            return;
        }

        RepositorySetting repository;

        if (_editingRepository is null)
        {
            repository = new RepositorySetting { Url = url };
            _settings.Repositories.Add(repository);
        }
        else
        {
            repository = _editingRepository;

            if (!string.Equals(repository.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                var oldUrl = repository.Url;
                repository.Url = url;
                repository.Name = string.Empty;
                repository.PublicKeyPem = string.Empty;
                repository.KeyFingerprint = string.Empty;
                repository.LastSync = null;
                _synced.Remove(oldUrl);
                _keyChanges.Remove(oldUrl);
            }
        }

        _store.Save(_settings);
        _editingRepository = null;
        ModalLayer.Visibility = Visibility.Collapsed;
        await SyncRepositoryAsync(repository, trustNewKey: true);
    }

    private async Task SyncRepositoryAsync(RepositorySetting repository, bool trustNewKey)
    {
        RepositoryStatus.Text = "Syncing repository…";
        var result = await _repositories.SyncAsync(repository);

        if (!result.Ok || result.Index is null)
        {
            if (result.KeyChanged
                && result.PublicKeyPem is not null
                && result.Fingerprint is not null)
            {
                _keyChanges[repository.Url] = (result.PublicKeyPem, result.Fingerprint);
            }

            RepositoryStatus.Text = result.Message;
            RenderAvailablePlugins();
            if (ModalLayer.Visibility == Visibility.Visible)
            {
                RenderManageRepositories();
            }

            return;
        }

        _keyChanges.Remove(repository.Url);
        repository.Name = result.Index.Name;
        repository.PublicKeyPem = result.PublicKeyPem ?? repository.PublicKeyPem;
        repository.KeyFingerprint = result.Fingerprint ?? repository.KeyFingerprint;
        repository.LastSync = DateTimeOffset.UtcNow;
        _store.Save(_settings);
        _synced[repository.Url] = result.Index;
        RepositoryStatus.Text = result.Message;
        RenderPluginCards();
        RenderAvailablePlugins();
        if (ModalLayer.Visibility == Visibility.Visible)
        {
            RenderManageRepositories();
        }
    }

    private async Task TrustRepositoryAsync(RepositorySetting repository, string publicKeyPem, string fingerprint)
    {
        repository.PublicKeyPem = publicKeyPem;
        repository.KeyFingerprint = fingerprint;
        repository.LastSync = null;
        _store.Save(_settings);
        _keyChanges.Remove(repository.Url);
        await SyncRepositoryAsync(repository, trustNewKey: true);
    }

    private void OpenPluginDetails(RepositorySetting repository, RepositoryIndex index, RepositoryPlugin plugin)
    {
        var app = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
        var compatible = RepositoryCompatibility.IsCompatible(plugin, app, out var reason);
        var installed = _inventory.FirstOrDefault(item =>
            string.Equals(item.Id, plugin.Id, StringComparison.Ordinal));
        var installedVersion = installed?.Version;
        var upToDate = string.Equals(installedVersion, plugin.Version, StringComparison.Ordinal);
        var update = installed is not null && !upToDate;
        var canInstall = compatible && !upToDate;

        _pendingInstall = canInstall ? (repository, index, plugin) : null;
        _integrationConsentConfirmed = false;

        ConsentTitle.Text = $"{plugin.Name} {plugin.Version}";
        ConsentBody.Children.Clear();

        ConsentPrimaryButton.Content = upToDate ? "Installed" : update ? "Update" : "Install";
        ConsentPrimaryButton.IsEnabled = true;
        ConsentPrimaryButton.Visibility = canInstall ? Visibility.Visible : Visibility.Collapsed;
        ConsentSecondaryButton.Content = canInstall ? "Cancel" : "Close";

        if (!string.IsNullOrWhiteSpace(installed?.Description))
        {
            ConsentBody.Children.Add(new TextBlock
            {
                Text = installed.Description,
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        AddConsentLine("Source", string.IsNullOrWhiteSpace(repository.Name)
            ? repository.Url
            : $"{repository.Name} · {repository.Url}");
        AddConsentLine("Version", update
            ? $"{plugin.Version} (installed: {installedVersion})"
            : plugin.Version);
        AddConsentLine("Kind", plugin.Kind);
        AddConsentLine("Compatibility", compatible ? $"Supported on Chirpdesk {app}" : reason);
        AddConsentLine("Archive", $"{plugin.Size:N0} bytes · {plugin.Sha256}");
        AddConsentLine("Repository key", repository.KeyFingerprint);


        var alternatives = FindAlternativeSources(repository, plugin.Id);

        if (alternatives.Count > 0)
        {
            ConsentBody.Children.Add(new TextBlock
            {
                Text = "Multiple repositories offer this plugin. Choose the source explicitly:",
                FontSize = 12,
                Foreground = Token("CdText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 6),
            });

            var choices = new WrapPanel();
            choices.Children.Add(SourceChoice(repository, index, plugin, selected: true));

            foreach (var alternative in alternatives)
            {
                choices.Children.Add(SourceChoice(alternative.Repository, alternative.Index, alternative.Plugin, selected: false));
            }

            ConsentBody.Children.Add(choices);
        }

        ConsentBody.Children.Add(new TextBlock
        {
            Text = "Permissions",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("CdText"),
            Margin = new Thickness(0, 14, 0, 4),
        });

        if (plugin.Permissions is not { Count: > 0 })
        {
            ConsentBody.Children.Add(new TextBlock
            {
                Text = "No additional permissions.",
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
            });
        }
        else
        {
            foreach (var permission in plugin.Permissions)
            {
                ConsentBody.Children.Add(new TextBlock
                {
                    Text = $"\u2022 {permission}: {PermissionDescription(permission)}",
                    FontSize = 12,
                    Foreground = Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }

        ConsentBody.Children.Add(new TextBlock
        {
            Text = "Chirpdesk verifies the signed index and the archive before writing anything. Plugin code is not loaded in-process; v1 plugins are data only.",
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        });

        ConsentLayer.Visibility = Visibility.Visible;
    }

    private void OnCancelConsent(object sender, RoutedEventArgs e)
    {
        _pendingInstall = null;
        _integrationConsentConfirmed = false;
        ConsentLayer.Visibility = Visibility.Collapsed;
    }

    private async void OnConfirmConsent(object sender, RoutedEventArgs e)
    {
        if (_pendingInstall is not { } pending)
        {
            ConsentLayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (string.Equals(pending.Plugin.Kind, PluginLoader.IntegrationKind, StringComparison.Ordinal))
        {
            if (!_integrationConsentConfirmed)
            {
                ShowIntegrationConsent(pending.Plugin);
                return;
            }
        }

        _pendingInstall = null;
        _integrationConsentConfirmed = false;
        ConsentLayer.Visibility = Visibility.Collapsed;
        await InstallPluginAsync(pending.Repository, pending.Index, pending.Plugin);
    }

    /// <summary>
    /// The second gate for an integration. The normal sheet describes data; this one says plainly
    /// that a separate program will run with the user's own file access, and it cannot be
    /// dismissed with the primary button until the checkbox is ticked.
    /// </summary>
    private void ShowIntegrationConsent(RepositoryPlugin plugin)
    {
        _integrationConsentConfirmed = false;
        ConsentTitle.Text = $"Run {plugin.Name} as a separate program?";
        ConsentBody.Children.Clear();

        ConsentBody.Children.Add(new TextBlock
        {
            Text = $"{plugin.Name} installs an executable that Chirpdesk starts as its own process. "
                + "It is not sandboxed: it runs with your Windows account's file access and can use the network. "
                + "Chirpdesk only passes it a data folder and a private pipe.",
            FontSize = 12,
            Foreground = Token("CdText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        ConsentBody.Children.Add(new TextBlock
        {
            Text = "Permissions",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("CdText"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        if (plugin.Permissions is not { Count: > 0 })
        {
            ConsentBody.Children.Add(new TextBlock
            {
                Text = "The helper declares no host permissions. It can still work on its own network.",
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var permission in plugin.Permissions)
            {
                ConsentBody.Children.Add(new TextBlock
                {
                    Text = $"• {permission}: {PermissionDescription(permission)}",
                    FontSize = 12,
                    Foreground = Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }

        var repository = _pendingInstall?.Repository;
        AddConsentLine("Source", repository is null
            ? plugin.Id
            : string.IsNullOrWhiteSpace(repository.Name)
                ? repository.Url
                : $"{repository.Name} · {repository.Url}");
        AddConsentLine("Plugin", $"{plugin.Id} {plugin.Version}");
        AddConsentLine("Repository key", repository?.KeyFingerprint);
        AddConsentLine("Archive SHA-256", plugin.Sha256);

        ConsentBody.Children.Add(new TextBlock
        {
            Text = @"The helper's session data lives in %APPDATA%\Chirpdesk\integrations\" + plugin.Id
                + ". Uninstall offers to remove it.",
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 10),
        });

        var checkbox = new CheckBox
        {
            Content = "I understand this runs a separate program with my file access.",
            Foreground = Token("CdText"),
            FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
        };

        ConsentBody.Children.Add(checkbox);
        checkbox.Checked += (_, _) =>
        {
            _integrationConsentConfirmed = true;
            ConsentPrimaryButton.IsEnabled = true;
        };
        checkbox.Unchecked += (_, _) =>
        {
            _integrationConsentConfirmed = false;
            ConsentPrimaryButton.IsEnabled = false;
        };

        ConsentPrimaryButton.Content = "Install integration";
        ConsentPrimaryButton.IsEnabled = false;
        ConsentPrimaryButton.Visibility = Visibility.Visible;
        ConsentSecondaryButton.Content = "Cancel";
        ConsentLayer.Visibility = Visibility.Visible;
    }

    private List<(RepositorySetting Repository, RepositoryIndex Index, RepositoryPlugin Plugin)> FindAlternativeSources(
        RepositorySetting selected,
        string pluginId)
    {
        var alternatives = new List<(RepositorySetting, RepositoryIndex, RepositoryPlugin)>();

        foreach (var repository in _settings.Repositories)
        {
            if (string.Equals(repository.Url, selected.Url, StringComparison.OrdinalIgnoreCase)
                || !_synced.TryGetValue(repository.Url, out var index))
            {
                continue;
            }

            var plugin = index.Plugins.FirstOrDefault(item =>
                string.Equals(item.Id, pluginId, StringComparison.Ordinal));

            if (plugin is not null)
            {
                alternatives.Add((repository, index, plugin));
            }
        }

        return alternatives;
    }

    private Button SourceChoice(
        RepositorySetting repository,
        RepositoryIndex index,
        RepositoryPlugin plugin,
        bool selected)
    {
        var button = new Button
        {
            Content = $"{repository.Name ?? repository.Url} · {plugin.Version}",
            Style = (Style)FindResource(selected ? "PillButton" : "GhostButton"),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 6),
        };
        button.Click += (_, _) => OpenPluginDetails(repository, index, plugin);
        return button;
    }

    private void AddConsentLine(string label, string? value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
        });

        var text = new TextBlock
        {
            Text = value ?? "\u2014",
            FontSize = 11,
            Foreground = Token("CdText"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        ConsentBody.Children.Add(grid);
    }

    private static string PermissionDescription(string permission) => permission switch
    {
        "css" => "inject a stylesheet into the plugin's own service page",
        "notifications" => "receive notification events from its service",
        "filters" => "contribute ad and tracker host rules",
        "tools" => "declare a host-rendered tool panel (bundled only)",
        "read-storage" => "read per-service size reports (bundled only)",
        "trim-cache" => "request cache trims (bundled only)",
        "storage" => "own a profile directory (bundled only)",
        "inject-js" => "run JavaScript in the page origin (not granted in v1)",
        "process" => "start a helper process (integration only)",
        "unread" => "set its unread badge count",
        "commands" => "declare command-bar entries and receive the text you type",
        _ => "unknown permission; installation will be refused",
    };

    private async Task InstallPluginAsync(RepositorySetting repository, RepositoryIndex index, RepositoryPlugin plugin)
    {
        RepositoryStatus.Text = $"Installing {plugin.Name}…";
        await StopIntegrationAsync(plugin.Id);
        var result = await _repositories.InstallAsync(repository, index, plugin);
        RepositoryStatus.Text = result.Message;
        if (result.Ok)
        {
            _reloadPlugins();
        }
    }

    private void RemoveRepository(RepositorySetting repository)
    {
        _settings.Repositories.RemoveAll(item => string.Equals(item.Url, repository.Url, StringComparison.OrdinalIgnoreCase));
        _store.Save(_settings);
        _synced.Remove(repository.Url);
        _keyChanges.Remove(repository.Url);
        RenderPluginCards();
        RenderAvailablePlugins();
        RenderManageRepositories();
    }

    private void SetRepositoryAutoUpdate(RepositorySetting repository, bool enabled)
    {
        repository.AutoUpdate = enabled;
        _store.Save(_settings);
    }

    private void OnOpenPluginsFolder(object sender, RoutedEventArgs e) =>
        OpenFolder(AppPaths.UserPluginsDirectory);

    private void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn($"could not open the folder: {ex.Message}");
        }
    }

    private void OnClosePluginSettings(object sender, RoutedEventArgs e) =>
        PluginSettingsLayer.Visibility = Visibility.Collapsed;

    private void OpenServiceSettings(string definitionId, string name)
    {
        PluginSettingsTitle.Text = $"{name} settings";
        PluginSettingsBody.Children.Clear();

        var instances = _settings.Services
            .Where(instance => string.Equals(instance.DefinitionId, definitionId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (instances.Count == 0)
        {
            PluginSettingsBody.Children.Add(new TextBlock
            {
                Text = "No accounts yet. Add one to create a separate profile and session.",
                Foreground = Token("CdTextMuted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        foreach (var instance in instances)
        {
            var row = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
            };

            var content = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var labels = new StackPanel();
            labels.Children.Add(new TextBlock
            {
                Text = instance.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Foreground = Token("CdText"),
            });
            labels.Children.Add(new TextBlock
            {
                Text = _registry.StateOf(instance.InstanceId) == RuntimeState.Warm ? "Warm" : "Cold",
                FontSize = 10,
                Foreground = Token("CdTextMuted"),
            });
            header.Children.Add(labels);

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 5, 10, 5),
            };
            remove.Click += async (_, _) =>
            {
                await _removeInstance!(instance.InstanceId);
                PluginSettingsLayer.Visibility = Visibility.Collapsed;
                _reloadPlugins();
            };
            Grid.SetColumn(remove, 1);
            header.Children.Add(remove);
            content.Children.Add(header);

            var switches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            switches.Children.Add(Switch(
                "Warm at boot",
                instance.WarmAtBoot,
                value => SaveInstance(instance.InstanceId, item => item.WarmAtBoot = value)));
            switches.Children.Add(Switch(
                "Mute",
                instance.Mute,
                value => SaveInstance(instance.InstanceId, item => item.Mute = value)));
            content.Children.Add(switches);

            row.Child = content;
            PluginSettingsBody.Children.Add(row);
        }

        var addAccount = new Button
        {
            Content = "Add account",
            Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
        };
        addAccount.Click += async (_, _) =>
        {
            PluginSettingsLayer.Visibility = Visibility.Collapsed;
            var definition = _catalog.Find(definitionId);
            if (definition is not null)
            {
                await _addAccount(definition);
                Reload();
            }
        };
        PluginSettingsBody.Children.Add(addAccount);

        PluginSettingsLayer.Visibility = Visibility.Visible;
    }

    private CheckBox Switch(string label, bool value, Action<bool> changed)
    {
        var toggle = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Style = (Style)FindResource("CdSwitch"),
            Margin = new Thickness(0, 0, 16, 0),
        };
        toggle.Checked += (_, _) => changed(true);
        toggle.Unchecked += (_, _) => changed(false);
        return toggle;
    }

    private void SaveInstance(string instanceId, Action<ServiceInstance> apply)
    {
        var settings = _store.Load();
        var instance = settings.Services.FirstOrDefault(item =>
            string.Equals(item.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

        if (instance is null)
        {
            return;
        }

        apply(instance);
        _store.Save(settings);
        _settings = settings;
    }

    private void OpenPluginSettings(InstalledPlugin plugin)
    {
        if (string.Equals(plugin.Id, "tool-cleaner", StringComparison.Ordinal))
        {
            PluginSettingsTitle.Text = "Disk cleaner";
            RenderStorageSettings(plugin);
            PluginSettingsLayer.Visibility = Visibility.Visible;
            return;
        }

        PluginSettingsTitle.Text = $"{plugin.Name} settings";
        PluginSettingsBody.Children.Clear();
        PluginSettingsBody.Children.Add(new TextBlock
        {
            Text = plugin.Source is { } source
                ? $"Version {plugin.Version} from {source.RepositoryUrl}"
                : $"Version {plugin.Version ?? "unknown"}",
            FontSize = 12,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        PluginSettingsBody.Children.Add(new TextBlock
        {
            Text = "Activation and removal are on the plugin card.",
            FontSize = 12,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
        });
        PluginSettingsLayer.Visibility = Visibility.Visible;
    }

    private void RenderStorageSettings(InstalledPlugin plugin)
    {
        PluginSettingsBody.Children.Clear();

        var usage = ProfileScanner.ScanAll();
        var reclaimedMb = _settings.ReclaimedBytes / (1024d * 1024d);

        PluginSettingsBody.Children.Add(new TextBlock
        {
            Text = $"Reclaimed {reclaimedMb:0.0} MB since install",
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
            Margin = new Thickness(0, 0, 0, 8),
        });

        foreach (var profile in usage)
        {
            var names = _settings.Services
                .Where(instance => string.Equals(instance.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Select(instance => instance.Name)
                .ToList();

            var warm = _settings.Services
                .Where(instance => string.Equals(instance.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Any(instance => _registry.StateOf(instance.InstanceId) == RuntimeState.Warm);

            PluginSettingsBody.Children.Add(new TextBlock
            {
                Text = $"{(names.Count > 0 ? string.Join(", ", names) : profile.ProfileId)}  ·  "
                    + $"data {Megabytes(profile.DataBytes)}, cache {Megabytes(profile.CacheBytes)}  ·  "
                    + (warm ? "warm" : "cold"),
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }

        if (usage.Count == 0)
        {
            PluginSettingsBody.Children.Add(new TextBlock
            {
                Text = "No profiles yet.",
                FontSize = 12,
                Foreground = Token("CdTextMuted"),
            });
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 10) };
        var trim = new Button
        {
            Content = "Trim caches",
            Style = (Style)FindResource("PillButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 6, 6),
        };
        trim.Click += (_, _) => TrimCaches(plugin);
        actions.Children.Add(trim);

        var scan = new Button
        {
            Content = "Scan for leftovers",
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 6, 6),
        };
        scan.Click += (_, _) => ScanOrphans(plugin);
        actions.Children.Add(scan);

        var folder = new Button
        {
            Content = "Open profile folder",
            Style = (Style)FindResource("GhostButton"),
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 6, 6),
        };
        folder.Click += (_, _) => OpenFolder(AppPaths.ProfilesRoot);
        actions.Children.Add(folder);
        PluginSettingsBody.Children.Add(actions);

        PluginSettingsBody.Children.Add(new TextBlock
        {
            Text = "Cookies, Local Storage, IndexedDB and Service Worker are the session and are never trimmed.",
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        PluginSettingsBody.Children.Add(new TextBlock
        {
            Text = !_cleanerScanned
                ? "Leftovers not scanned yet."
                : _cleanerOrphans.Count == 0
                    ? "Scan complete: no leftovers found."
                    : $"{_cleanerOrphans.Count} leftover(s) found. Nothing is pre-selected.",
            FontSize = 11,
            Foreground = Token("CdTextMuted"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        foreach (var orphan in _cleanerOrphans)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = $"{orphan.ProfileId ?? Path.GetFileName(orphan.Path)}  ·  {orphan.App}  ·  {Megabytes(orphan.Bytes)}",
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var recycle = new Button
            {
                Content = "Recycle",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(8, 4, 8, 4),
                Tag = orphan,
            };
            recycle.Click += (_, _) => RecycleOrphan(plugin, orphan);
            Grid.SetColumn(recycle, 1);
            row.Children.Add(recycle);
            PluginSettingsBody.Children.Add(row);
        }
    }

    private void TrimCaches(InstalledPlugin plugin)
    {
        var candidates = PlanTrim();
        long reclaimed = 0;

        foreach (var candidate in candidates)
        {
            reclaimed += CacheTrimmer.Trim(candidate, _log);
        }

        if (reclaimed > 0)
        {
            _settings.ReclaimedBytes += reclaimed;
            _store.Save(_settings);
        }

        RenderStorageSettings(plugin);
    }

    private IReadOnlyList<TrimCandidate> PlanTrim()
    {
        var usage = ProfileScanner.ScanAll();
        var profiles = new List<ProfileCache>();

        foreach (var profile in usage)
        {
            var warm = _settings.Services
                .Where(instance => string.Equals(instance.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Any(instance => _registry.StateOf(instance.InstanceId) == RuntimeState.Warm);

            profiles.Add(new ProfileCache(
                profile.ProfileId,
                Path.Combine(AppPaths.ProfilesRoot, profile.ProfileId),
                profile.CacheBytes,
                Disposed: !warm,
                profile.LastWrite));
        }

        return TrimPlanner.Plan(
            profiles,
            _settings.CacheBudgetMb * 1024L * 1024L,
            _settings.GlobalCacheBudgetMb * 1024L * 1024L).Candidates;
    }

    private void ScanOrphans(InstalledPlugin plugin)
    {
        var referenced = _settings.Services
            .Select(instance => instance.ProfileId ?? instance.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _cleanerOrphans = OrphanScanner.Scan(
            AppPaths.ProfilesRoot,
            referenced,
            KnownRoots.ForThisUser(),
            KnownRoots.IsInUse).ToList();
        _cleanerScanned = true;
        RenderStorageSettings(plugin);
    }

    private void RecycleOrphan(InstalledPlugin plugin, OrphanItem orphan)
    {
        if (MessageBox.Show(
                $"Move '{orphan.Path}' to the Recycle Bin?",
                "Remove leftover",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        if (OrphanDeleter.Delete(orphan, _log))
        {
            _cleanerOrphans.Remove(orphan);
            RenderStorageSettings(plugin);
        }
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0.0} MB";

    private async Task RunIntegrationAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException or ObjectDisposedException)
        {
            _log.Error("plugins page: integration action failed", ex);
            MessageBox.Show(ex.Message, "Integration", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Reload();
    }

    private async Task StopIntegrationAsync(string pluginId)
    {
        if (_integrations is null)
        {
            return;
        }

        try
        {
            await _integrations.RemoveAsync(pluginId);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _log.Warn($"could not stop integration '{pluginId}': {ex.Message}");
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => _back();

    private System.Windows.Media.Brush Token(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;
}
