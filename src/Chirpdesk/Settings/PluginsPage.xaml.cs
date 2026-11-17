using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Settings;
using Chirpdesk.Hosting;
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
    private readonly SettingsStore _store;
    private readonly ServiceRegistry _registry;
    private readonly ILog _log;
    private readonly Func<ServiceDefinition, Task> _enable;
    private readonly Func<ServiceDefinition, Task> _addAccount;
    private readonly Func<ServiceDefinition, Task> _disable;
    private readonly Action _back;
    private AppSettings _settings;

    internal PluginsPage(
        ServiceCatalog catalog,
        SettingsStore store,
        ServiceRegistry registry,
        ILog log,
        Func<ServiceDefinition, Task> enable,
        Func<ServiceDefinition, Task> addAccount,
        Func<ServiceDefinition, Task> disable,
        Action back)
    {
        InitializeComponent();
        _catalog = catalog;
        _store = store;
        _registry = registry;
        _log = log;
        _enable = enable;
        _addAccount = addAccount;
        _disable = disable;
        _back = back;
        _settings = store.Load();
        PageIcon.Content = UiIcons.Create("plugins", Token("CdAccent"), 28);
        Reload();
    }

    public void Reload()
    {
        _settings = _store.Load();
        PluginsPanel.Children.Clear();

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
