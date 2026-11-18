using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;
using Chirpdesk.Hosting;
using Chirpdesk.Repository;
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
    private readonly HashSet<string> _removedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RepositoryIndex> _synced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Pem, string Fingerprint)> _keyChanges = new(StringComparer.OrdinalIgnoreCase);
    private RepositorySetting? _editingRepository;
    private (RepositorySetting Repository, RepositoryIndex Index, RepositoryPlugin Plugin)? _pendingInstall;
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
        Action reloadPlugins)
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
        _settings = store.Load();
        PageIcon.Content = UiIcons.Create("plugins", Token("CdAccent"), 28);
        Reload();
    }

    public void Reload()
    {
        _settings = _store.Load();
        RenderPluginCards();
        RenderAvailablePlugins();

        foreach (var repository in _settings.Repositories
            .Where(item => item.AutoUpdate
                && (item.LastSync is null || DateTimeOffset.UtcNow - item.LastSync > RepositoryService.CacheTtl))
            .ToList())
        {
            _ = SyncRepositoryAsync(repository, trustNewKey: true);
        }
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

            if (installedPlugin?.Source is { } serviceSource)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"v{serviceSource.Version} · {serviceSource.RepositoryUrl}",
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

            if (installedPlugin is { Bundled: false } installedService)
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
                    updateButton.Click += async (_, _) =>
                    {
                        RepositoryStatus.Text = $"Updating {definition.Name}…";
                        var result = await _repositories.InstallAsync(candidate.Repository, candidate.Index, candidate.Plugin);
                        RepositoryStatus.Text = result.Message;
                        if (result.Ok)
                        {
                            _reloadPlugins();
                        }
                    };
                    actions.Children.Add(updateButton);
                }

                if (RepositoryService.HasBackup(installedService.Id))
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
                    Content = "Uninstall",
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
            var toggleable = plugin.Kind is "theme" or "filter";
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

            if (!string.IsNullOrWhiteSpace(plugin.Version) || plugin.Source is not null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = plugin.Source is { } source
                        ? $"v{plugin.Version} · {source.RepositoryUrl}"
                        : $"v{plugin.Version} · locally installed",
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

            if (!plugin.Bundled)
            {
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
                    updateButton.Click += async (_, _) =>
                    {
                        RepositoryStatus.Text = $"Updating {plugin.Name}…";
                        var result = await _repositories.InstallAsync(candidate.Repository, candidate.Index, candidate.Plugin);
                        RepositoryStatus.Text = result.Message;
                        if (result.Ok)
                        {
                            _reloadPlugins();
                        }
                    };
                    actions.Children.Add(updateButton);
                }

                if (RepositoryService.HasBackup(plugin.Id))
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
                    Content = "Uninstall",
                    Style = (Style)FindResource("GhostButton"),
                    Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                };
                uninstall.Click += async (_, _) =>
                {
                    if (await _uninstallPlugin(plugin.Id))
                    {
                        _removedPlugins.Add(plugin.Id);
                        _reloadPlugins();
                    }
                };
                actions.Children.Add(uninstall);
            }

            if (actions.Children.Count > 0)
            {
                content.Children.Add(actions);
            }

            card.Child = content;
            PluginsPanel.Children.Add(card);
        }
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
            RepositoryStatus.Text = "No repositories configured.";
            return;
        }

        var rendered = 0;

        foreach (var repository in _settings.Repositories)
        {
            if (!_synced.TryGetValue(repository.Url, out var index))
            {
                continue;
            }

            rendered++;
            var app = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

            foreach (var plugin in index.Plugins)
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
                var row = new Border
                {
                    Style = (Style)FindResource("Card"),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(12),
                };

                var content = new Grid();
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel();
                text.Children.Add(new TextBlock
                {
                    Text = $"{plugin.Name}  {plugin.Version}",
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Token("CdText"),
                });
                text.Children.Add(new TextBlock
                {
                    Text = $"{plugin.Kind} · {(plugin.Permissions is { Count: > 0 } ? string.Join(", ", plugin.Permissions) : "no permissions")}",
                    FontSize = 11,
                    Foreground = Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                });
                text.Children.Add(new TextBlock
                {
                    Text = !compatible
                        ? reason
                        : upToDate
                            ? "Installed and up to date."
                            : updateAvailable
                                ? $"Installed {installedVersion} · update available."
                                : $"From {repository.Name ?? repository.Url}",
                    FontSize = 11,
                    Foreground = compatible ? Token("CdAccent") : Token("CdTextMuted"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 0),
                });
                content.Children.Add(text);

                var install = new Button
                {
                    Content = !compatible
                        ? "Unsupported"
                        : upToDate
                            ? "Installed"
                            : updateAvailable
                                ? "Update"
                                : "Install",
                    IsEnabled = compatible && !upToDate,
                    Style = (Style)FindResource("PillButton"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(14, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                install.Click += (_, _) => OpenInstallConsent(repository, index, plugin);
                Grid.SetColumn(install, 1);
                content.Children.Add(install);

                row.Child = content;
                AvailablePluginsPanel.Children.Add(row);
            }
        }

        RepositoryStatus.Text = rendered == 0
            ? $"{_settings.Repositories.Count} repository(ies) configured. Sync one to see plugins."
            : $"Showing plugins from {rendered} synced repository(ies).";
    }

    private void RenderManageRepositories()
    {
        ManageRepositoriesPanel.Children.Clear();

        if (_settings.Repositories.Count == 0)
        {
            ManageRepositoriesPanel.Children.Add(new TextBlock
            {
                Text = "No repositories yet. Use Add Plugins Repo to add one.",
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

    private void OpenInstallConsent(RepositorySetting repository, RepositoryIndex index, RepositoryPlugin plugin)
    {
        _pendingInstall = (repository, index, plugin);
        ConsentTitle.Text = $"Install {plugin.Name} {plugin.Version}?";
        ConsentBody.Children.Clear();

        AddConsentLine("Source", string.IsNullOrWhiteSpace(repository.Name) ? repository.Url : repository.Name);
        AddConsentLine("Version", plugin.Version);
        AddConsentLine("Kind", plugin.Kind);
        AddConsentLine("Repository key", repository.KeyFingerprint);
        AddConsentLine("Archive SHA-256", plugin.Sha256);

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
        ConsentLayer.Visibility = Visibility.Collapsed;
    }

    private async void OnConfirmConsent(object sender, RoutedEventArgs e)
    {
        if (_pendingInstall is not { } pending)
        {
            return;
        }

        _pendingInstall = null;
        ConsentLayer.Visibility = Visibility.Collapsed;
        await InstallPluginAsync(pending.Repository, pending.Index, pending.Plugin);
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
        button.Click += (_, _) => OpenInstallConsent(repository, index, plugin);
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
        _ => "unknown permission; installation will be refused",
    };

    private async Task InstallPluginAsync(RepositorySetting repository, RepositoryIndex index, RepositoryPlugin plugin)
    {
        RepositoryStatus.Text = $"Installing {plugin.Name}…";
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
        RenderAvailablePlugins();
        RenderManageRepositories();
    }

    private void SetRepositoryAutoUpdate(RepositorySetting repository, bool enabled)
    {
        repository.AutoUpdate = enabled;
        _store.Save(_settings);
    }

    private void OnOpenPluginsFolder(object sender, RoutedEventArgs e)
    {
        var path = AppPaths.UserPluginsDirectory;
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
            _log.Warn($"could not open the plugins folder: {ex.Message}");
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => _back();

    private System.Windows.Media.Brush Token(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;
}
