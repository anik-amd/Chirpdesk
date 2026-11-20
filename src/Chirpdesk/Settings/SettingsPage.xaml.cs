using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Storage;
using Chirpdesk.Core.Theming;
using Chirpdesk.Hosting;
using Chirpdesk.Interop;
using Chirpdesk.Storage;
using Chirpdesk.Ui;
using Chirpdesk.Updates;

namespace Chirpdesk.Settings;

/// <summary>
/// The only settings surface: services/instances, add-from-definition, appearance, autostart,
/// DND and storage. Changes save immediately; no tabs or sidebar.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly SettingsStore _store;
    private readonly ServiceCatalog _catalog;
    private readonly ServiceRegistry _registry;
    private readonly HostWindow _host;
    private readonly IReadOnlyList<ThemeFlavor> _themeFlavors;
    private readonly ILog _log;
    private readonly Action _applyTheme;
    private readonly Action? _stateChanged;
    private readonly Action _back;
    private readonly UpdateService _updates;
    private readonly Func<UpdateCheckResult, Task<bool>> _installUpdate;
    private readonly Action<bool> _applyEnterToSend;
    private UpdateCheckResult? _pendingUpdate;
    private AppSettings _settings;
    private bool _loading;

    internal SettingsPage(
        SettingsStore store,
        ServiceCatalog catalog,
        IReadOnlyList<PanelDefinition> panels,
        ServiceRegistry registry,
        HostWindow host,
        IReadOnlyList<ThemeFlavor> themeFlavors,
        ILog log,
        Action applyTheme,
        Action? stateChanged,
        Action back,
        UpdateService updates,
        Func<UpdateCheckResult, Task<bool>> installUpdate,
        Action<bool> applyEnterToSend)
    {
        InitializeComponent();
        _store = store;
        _catalog = catalog;
        _registry = registry;
        _host = host;
        _themeFlavors = themeFlavors;
        _log = log;
        _applyTheme = applyTheme;
        _stateChanged = stateChanged;
        _back = back;
        _updates = updates;
        _installUpdate = installUpdate;
        _applyEnterToSend = applyEnterToSend;
        _settings = store.Load();

        PageIcon.Content = UiIcons.Create("settings", Token("CdAccent"), 24);
        RenderThemes();

        SetDark(dark: false);
        Reload();
    }

    public void Reload()
    {
        _settings = _store.Load();
        LoadControls();
    }

    public void SetDark(bool dark)
    {
        TextElement.SetForeground(Root, Token("CdText"));

        if (IsLoaded)
        {
            Reload();
        }
    }

    private System.Windows.Media.Brush Token(string key) =>
        TryFindResource(key) as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;

    private void RenderThemes()
    {
        LightSchemesPanel.Children.Clear();
        DarkSchemesPanel.Children.Clear();

        foreach (var flavor in _themeFlavors.OrderBy(flavor => flavor.Name, StringComparer.OrdinalIgnoreCase))
        {
            var card = new RadioButton
            {
                GroupName = string.Equals(flavor.Mode, "light", StringComparison.Ordinal) ? "LightFlavor" : "DarkFlavor",
                Style = (Style)FindResource("ChoiceCard"),
                Tag = flavor,
            };
            AutomationProperties.SetName(card, flavor.Name);

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Background = BrushFromHex(flavor.Swatch),
                BorderBrush = Token("CdRule"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = flavor.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
            });

            card.Content = content;
            card.Checked += OnFlavorChoice;

            if (string.Equals(flavor.Mode, "light", StringComparison.Ordinal))
            {
                LightSchemesPanel.Children.Add(card);
            }
            else
            {
                DarkSchemesPanel.Children.Add(card);
            }
        }
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    private void OnFlavorChoice(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: ThemeFlavor flavor })
        {
            return;
        }

        if (string.Equals(flavor.Mode, "light", StringComparison.Ordinal))
        {
            _settings.LightTheme = flavor.Id;
        }
        else
        {
            _settings.DarkTheme = flavor.Id;
        }

        Save();
        _applyTheme();
    }

    private void LoadControls()
    {
        _loading = true;

        ThemeSystem.IsChecked = !string.Equals(_settings.Theme, "light", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_settings.Theme, "dark", StringComparison.OrdinalIgnoreCase);
        ThemeLight.IsChecked = string.Equals(_settings.Theme, "light", StringComparison.OrdinalIgnoreCase);
        ThemeDark.IsChecked = string.Equals(_settings.Theme, "dark", StringComparison.OrdinalIgnoreCase);

        foreach (var card in LightSchemesPanel.Children.OfType<RadioButton>())
        {
            card.IsChecked = card.Tag is ThemeFlavor flavor
                && string.Equals(flavor.Id, _settings.LightTheme, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var card in DarkSchemesPanel.Children.OfType<RadioButton>())
        {
            card.IsChecked = card.Tag is ThemeFlavor flavor
                && string.Equals(flavor.Id, _settings.DarkTheme, StringComparison.OrdinalIgnoreCase);
        }

        var catppuccin = PluginState.IsEnabled(_settings, PluginState.CatppuccinId);
        LightSchemesPanel.IsEnabled = catppuccin;
        DarkSchemesPanel.IsEnabled = catppuccin;

        AutostartBox.IsChecked = _settings.Autostart;
        MinimizeToTrayBox.IsChecked = _settings.MinimizeToTray;
        CloseToTrayBox.IsChecked = _settings.CloseToTray;
        EnterToSendBox.IsChecked = _settings.EnterToSend;
        UpdateAutoBox.IsChecked = _settings.UpdateAutoCheck;

        var dndIndex = 0;
        if (_settings.DndUntil is { } dnd && dnd > DateTimeOffset.UtcNow)
        {
            var minutes = (dnd - DateTimeOffset.UtcNow).TotalMinutes;
            dndIndex = minutes <= 31 ? 1 : minutes <= 61 ? 2 : 3;
        }

        DndOff.IsChecked = dndIndex == 0;
        Dnd30.IsChecked = dndIndex == 1;
        Dnd60.IsChecked = dndIndex == 2;
        DndTomorrow.IsChecked = dndIndex == 3;

        DndStatus.Text = _settings.DndUntil is { } until && until > DateTimeOffset.UtcNow
            ? $"Toasts are quiet until {until.ToLocalTime():ddd HH:mm}. Unread still counts."
            : "Toasts are on. Notifications still appear in Action Center while quiet.";

        _host.MinimizeToTray = _settings.MinimizeToTray;
        _host.CloseToTray = _settings.CloseToTray;

        RailAlways.IsChecked = !string.Equals(_settings.RailMode, "hover", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_settings.RailMode, "hidden", StringComparison.OrdinalIgnoreCase);
        RailHover.IsChecked = string.Equals(_settings.RailMode, "hover", StringComparison.OrdinalIgnoreCase);
        RailHidden.IsChecked = string.Equals(_settings.RailMode, "hidden", StringComparison.OrdinalIgnoreCase);
        _host.SetRailMode(_settings.RailMode);

        _loading = false;
    }




    private void OnThemeChoice(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string theme })
        {
            return;
        }

        _settings.Theme = theme;
        Save();
        _applyTheme();
    }

    private void OnWindowBehaviourChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _settings.CloseToTray = CloseToTrayBox.IsChecked == true;
        _host.MinimizeToTray = _settings.MinimizeToTray;
        _host.CloseToTray = _settings.CloseToTray;
        Save();
    }

    private void OnRailChoice(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string mode })
        {
            return;
        }

        _settings.RailMode = mode;
        _host.SetRailMode(mode);
        Save();
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = AutostartBox.IsChecked == true;
        var executable = Environment.ProcessPath;

        if (executable is null || !Interop.Autostart.TrySet(enabled, executable, _log))
        {
            _loading = true;
            AutostartBox.IsChecked = !enabled;
            _loading = false;
            MessageBox.Show("Windows refused the autostart change.", "Autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Autostart = enabled;
        Save();
    }

    private void OnUpdateAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.UpdateAutoCheck = UpdateAutoBox.IsChecked == true;
        Save();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "Checking GitHub for a signed stable release…";
        UpdateVersion.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;

        try
        {
            var result = await _updates.CheckAsync();
            _pendingUpdate = result.Available ? result : null;
            UpdateStatus.Text = result.Message;

            if (result.Available)
            {
                UpdateVersion.Text = $"Version {result.Version}";
                UpdateVersion.Visibility = Visibility.Visible;
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TaskCanceledException)
        {
            _log.Warn($"update check failed: {ex.Message}");
            UpdateStatus.Text = "Could not check for updates.";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Downloading and verifying the installer…";

        try
        {
            if (await _installUpdate(_pendingUpdate))
            {
                UpdateStatus.Text = "Update verified. Chirpdesk is restarting.";
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TaskCanceledException)
        {
            _log.Error("could not apply the update", ex);
        }

        UpdateStatus.Text = "Update did not apply. The current version is still running.";
        CheckUpdatesButton.IsEnabled = true;
        InstallUpdateButton.IsEnabled = true;
    }

    private void OnDndChoice(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string raw } || !int.TryParse(raw, out var index))
        {
            return;
        }

        _settings.DndUntil = index switch
        {
            1 => DateTimeOffset.UtcNow.AddMinutes(30),
            2 => DateTimeOffset.UtcNow.AddHours(1),
            3 => TomorrowMorning(),
            _ => null,
        };

        Save();
        _stateChanged?.Invoke();
        DndStatus.Text = _settings.DndUntil is { } until
            ? $"Toasts are quiet until {until.ToLocalTime():ddd HH:mm}. Unread still counts."
            : "Toasts are on. Notifications still appear in Action Center while quiet.";
    }

    private static DateTimeOffset TomorrowMorning()
    {
        var local = DateTimeOffset.Now.AddDays(1);
        var morning = new DateTimeOffset(local.Year, local.Month, local.Day, 8, 0, 0, local.Offset);
        return morning.ToUniversalTime();
    }









    private void Save()
    {
        _store.Save(_settings);
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0.0} MB";

    private void OnEnterToSendChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _applyEnterToSend(EnterToSendBox.IsChecked == true);
    }

    private void OnBack(object sender, RoutedEventArgs e) => _back();
}
