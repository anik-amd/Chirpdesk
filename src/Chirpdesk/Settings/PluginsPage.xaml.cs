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
    private readonly Dictionary<string, RepositoryIndex> _synced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Pem, string Fingerprint)> _keyChanges = new(StringComparer.OrdinalIgnoreCase);
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
        RepositoryService repositories)
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
        _settings = store.Load();
        PageIcon.Content = UiIcons.Create("plugins", Token("CdAccent"), 28);
        Reload();
    }

    public void Reload()
    {
        _settings = _store.Load();
        PluginsPanel.Children.Clear();
        RenderRepositories();

        foreach (var repository in _settings.Repositories
            .Where(item => item.AutoUpdate
                && (item.LastSync is null || DateTimeOffset.UtcNow - item.LastSync > RepositoryService.CacheTtl))
            .ToList())
        {
            _ = SyncRepositoryAsync(repository, trustNewKey: true);
        }

        foreach (var definition in _catalog.Definitions.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var instances = _settings.Services
                .Where(instance => string.Equals(instance.DefinitionId, definition.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var enabled = instances.Any(instance => instance.Enabled);
            var active = instances.Count(instance => instance.Enabled);

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
                Margin = new Thickness(0, 0, 0, 12),
                MinHeight = 34,
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var primary = new Button
            {
                Content = enabled ? "Add account" : "Activate",
                Width = 104,
                Margin = new Thickness(0, 0, 8, 0),
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
            };
            off.Click += async (_, _) => await Run(_disable, definition);
            actions.Children.Add(off);

            content.Children.Add(actions);
            card.Child = content;
            PluginsPanel.Children.Add(card);
        }

        foreach (var plugin in _inventory.Where(item => !string.Equals(item.Kind, PluginLoader.ServiceKind, StringComparison.Ordinal)))
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

            if (toggleable)
            {
                var toggle = new Button
                {
                    Content = pluginEnabled ? "Deactivate" : "Activate",
                    Width = 104,
                    Style = (Style)FindResource(pluginEnabled ? "GhostButton" : "PillButton"),
                };
                toggle.Click += async (_, _) => await RunPlugin(plugin.Id, !pluginEnabled);
                content.Children.Add(toggle);
            }

            card.Child = content;
            PluginsPanel.Children.Add(card);
        }
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

    private void RenderRepositories()
    {
        RepositoriesPanel.Children.Clear();
        AvailablePluginsPanel.Children.Clear();

        if (_settings.Repositories.Count == 0)
        {
            RepositoryStatus.Text = "No repositories configured.";
            return;
        }

        RepositoryStatus.Text = $"{_settings.Repositories.Count} repository(ies) configured.";

        foreach (var repository in _settings.Repositories.ToList())
        {
            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(12),
            };

            var content = new StackPanel();
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(repository.Name) ? repository.Url : repository.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
            };
            content.Children.Add(title);
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
                Margin = new Thickness(0, 4, 0, 8),
            });

            var auto = new CheckBox
            {
                Content = "Auto-update",
                IsChecked = repository.AutoUpdate,
                Style = (Style)FindResource("CdSwitch"),
                Margin = new Thickness(0, 0, 0, 8),
            };
            auto.Checked += (_, _) => SetRepositoryAutoUpdate(repository, true);
            auto.Unchecked += (_, _) => SetRepositoryAutoUpdate(repository, false);
            content.Children.Add(auto);

            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var sync = new Button
            {
                Content = "Sync",
                Style = (Style)FindResource("PillButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
            };
            sync.Click += async (_, _) => await SyncRepositoryAsync(repository, trustNewKey: true);
            actions.Children.Add(sync);

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(12, 6, 12, 6),
            };
            remove.Click += (_, _) => RemoveRepository(repository);
            actions.Children.Add(remove);

            if (_keyChanges.TryGetValue(repository.Url, out var pendingKey))
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"New key: {pendingKey.Fingerprint}\nOld key: {repository.KeyFingerprint}",
                    FontSize = 11,
                    Foreground = Token("CdAccent"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 6),
                });

                var trust = new Button
                {
                    Content = "Trust new key",
                    Style = (Style)FindResource("PillButton"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 0),
                };
                trust.Click += async (_, _) => await TrustRepositoryAsync(repository, pendingKey.Pem, pendingKey.Fingerprint);
                actions.Children.Add(trust);
            }

            content.Children.Add(actions);
            card.Child = content;
            RepositoriesPanel.Children.Add(card);

            if (_synced.TryGetValue(repository.Url, out var index))
            {
                RenderAvailablePlugins(repository, index);
            }
        }
    }

    private void RenderAvailablePlugins(RepositorySetting repository, RepositoryIndex index)
    {
        var app = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

        foreach (var plugin in index.Plugins)
        {
            var compatible = RepositoryCompatibility.IsCompatible(plugin, app, out var reason);
            var row = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
            };

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = $"{plugin.Name}  {plugin.Version}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{plugin.Kind} · {(plugin.Permissions is { Count: > 0 } ? string.Join(", ", plugin.Permissions) : "no permissions")}",
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });

            if (!compatible)
            {
                content.Children.Add(new TextBlock
                {
                    Text = reason,
                    FontSize = 11,
                    Foreground = Token("CdTextMuted"),
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            var install = new Button
            {
                Content = compatible ? "Install" : "Unsupported",
                IsEnabled = compatible,
                Style = (Style)FindResource("PillButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 8, 0, 0),
            };
            install.Click += async (_, _) => await InstallPluginAsync(repository, index, plugin);
            content.Children.Add(install);
            row.Child = content;
            AvailablePluginsPanel.Children.Add(row);
        }
    }

    private async void OnAddRepository(object sender, RoutedEventArgs e)
    {
        var url = RepositoryUrlBox.Text.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            RepositoryStatus.Text = "Enter an absolute https repository URL.";
            return;
        }

        if (_settings.Repositories.Any(item => string.Equals(item.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            RepositoryStatus.Text = "That repository is already added.";
            return;
        }

        var repository = new RepositorySetting { Url = url };
        _settings.Repositories.Add(repository);
        _store.Save(_settings);
        RepositoryUrlBox.Clear();
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
            RenderRepositories();
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
        RenderRepositories();
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

    private async Task InstallPluginAsync(RepositorySetting repository, RepositoryIndex index, RepositoryPlugin plugin)
    {
        RepositoryStatus.Text = $"Installing {plugin.Name}…";
        var result = await _repositories.InstallAsync(repository, index, plugin);
        RepositoryStatus.Text = result.Message;
        if (result.Ok)
        {
            RepositoryStatus.Text += " Restart Chirpdesk to load it.";
        }
    }

    private void SetRepositoryAutoUpdate(RepositorySetting repository, bool enabled)
    {
        repository.AutoUpdate = enabled;
        _store.Save(_settings);
    }

    private void RemoveRepository(RepositorySetting repository)
    {
        _settings.Repositories.RemoveAll(item => string.Equals(item.Url, repository.Url, StringComparison.OrdinalIgnoreCase));
        _store.Save(_settings);
        _synced.Remove(repository.Url);
        RenderRepositories();
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
