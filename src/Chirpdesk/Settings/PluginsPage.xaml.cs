using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Settings;
using Chirpdesk.Hosting;

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

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = definition.Name, FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x30, 0x36)) });
            label.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(definition.Description)
                    ? "Turn on to add this service; add another account for a second login."
                    : definition.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x65, 0x60)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            });
            label.Children.Add(new TextBlock
            {
                Text = enabled ? $"Active · {active} account(s)" : "Inactive",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x65, 0x60)),
            });
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var primary = new Button
            {
                Content = enabled ? "Add account" : "Activate",
                Width = 110,
                Margin = new Thickness(8, 0, 8, 0),
            };
            primary.Click += async (_, _) => await Run(enabled ? _addAccount : _enable, definition);
            Grid.SetColumn(primary, 1);
            row.Children.Add(primary);

            var off = new Button { Content = "Deactivate", Width = 86, IsEnabled = enabled };
            off.Click += async (_, _) => await Run(_disable, definition);
            Grid.SetColumn(off, 2);
            row.Children.Add(off);

            PluginsPanel.Children.Add(row);
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
}
