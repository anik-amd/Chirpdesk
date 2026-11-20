using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
using Chirpdesk.Core.Userscripts;
using Chirpdesk.Hosting;
using Chirpdesk.Interop;
using Chirpdesk.Storage;
using Chirpdesk.Ui;
using Chirpdesk.Updates;
using Chirpdesk.Userscripts;
using Microsoft.Win32;

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
    private readonly UserscriptService _userscripts;
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
        UserscriptService userscripts)
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
        _userscripts = userscripts;
        _settings = store.Load();
        _userscripts.Changed += (_, _) => RenderUserscripts();

        PageIcon.Content = UiIcons.Create("settings", Token("CdAccent"), 24);
        RenderThemes();

        SetDark(dark: false);
        Reload();
    }

    public void Reload()
    {
        _settings = _store.Load();
        LoadControls();
        RenderUserscripts();
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

    private void RenderUserscripts()
    {
        if (UserScriptsPanel is null)
        {
            return;
        }

        UserScriptsPanel.Children.Clear();
        var scripts = _userscripts.All();
        UserScriptsEmpty.Visibility = scripts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var installed in scripts)
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var head = new DockPanel();

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(
                        $"Remove {installed.Script.Name}? It stops running the next time its page reloads.",
                        "Userscript",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Question) == MessageBoxResult.OK)
                {
                    _userscripts.Remove(installed.Id);
                }
            };
            DockPanel.SetDock(remove, Dock.Right);
            head.Children.Add(remove);

            var toggle = new CheckBox
            {
                Content = Title(installed),
                IsChecked = installed.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Checked += (_, _) => _userscripts.SetEnabled(installed.Id, true);
            toggle.Unchecked += (_, _) => _userscripts.SetEnabled(installed.Id, false);
            head.Children.Add(toggle);
            card.Children.Add(head);

            var patterns = installed.Script.Matches.Concat(installed.Script.Includes).ToList();
            card.Children.Add(new TextBlock
            {
                Text = patterns.Count == 0
                    ? "No @match: this script runs nowhere."
                    : "Runs on: " + string.Join(", ", patterns),
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(22, 2, 0, 0),
            });

            if (!string.IsNullOrWhiteSpace(installed.SourceUrl))
            {
                card.Children.Add(new TextBlock
                {
                    Text = installed.SourceUrl,
                    FontSize = 10,
                    Foreground = Token("CdTextMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(22, 1, 0, 0),
                });
            }

            UserScriptsPanel.Children.Add(card);
        }
    }

    private async void OnAddUserscript(object sender, RoutedEventArgs e)
    {
        var url = PromptText(
            "Add userscript",
            "https://example.com/script.user.js",
            "Paste the https URL of a .user.js file. It is downloaded once; Chirpdesk never updates it silently.");

        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            var candidate = await UserscriptService.DownloadAsync(url);
            if (ConfirmUserscript(candidate))
            {
                _userscripts.Install(candidate);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or HttpRequestException
            or TaskCanceledException
            or FormatException)
        {
            MessageBox.Show(ex.Message, "Userscript", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnImportUserscript(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Userscript (*.user.js)|*.user.js|JavaScript (*.js)|*.js|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var candidate = UserscriptService.ReadFile(dialog.FileName);
            if (ConfirmUserscript(candidate))
            {
                _userscripts.Install(candidate);
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Userscript", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenUserscripts(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.UserscriptsDirectory);
        Process.Start(new ProcessStartInfo(AppPaths.UserscriptsDirectory) { UseShellExecute = true });
    }

    private void OnRescanUserscripts(object sender, RoutedEventArgs e) => _userscripts.Reload();

    /// <summary>
    /// The consent step. A userscript is third-party code inside a signed-in session, so the
    /// patterns, source and hash are shown before anything is written.
    /// </summary>
    private static bool ConfirmUserscript(UserscriptCandidate candidate)
    {
        var patterns = candidate.Script.Matches.Concat(candidate.Script.Includes).ToList();
        if (patterns.Count == 0)
        {
            MessageBox.Show(
                "This script has no @match or @include, so it would never run. Add a pattern before installing.",
                "Userscript",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var text =
            $"{Title(candidate.Script)}\n\n" +
            "Runs on:\n  " + string.Join("\n  ", patterns.Take(10)) + "\n\n" +
            "It runs in the page with your signed-in session: it can read anything you can see and send it anywhere. " +
            "Install only code you trust. Chirpdesk does not sign or auto-update userscripts.\n\n" +
            $"SHA-256:\n{candidate.Sha256}\n\nSource:\n{candidate.SourceUrl}";

        return MessageBox.Show(
            text,
            "Install this userscript?",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
    }

    private string? PromptText(string title, string placeholder, string description)
    {
        var window = new Window
        {
            Title = title,
            Width = 580,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = Token("CdCanvas"),
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Token("CdTextMuted"),
            Margin = new Thickness(0, 0, 0, 10),
        });

        var box = new TextBox { Text = placeholder, Padding = new Thickness(8, 6, 8, 6) };
        panel.Children.Add(box);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        var ok = new Button { Content = "Add", IsDefault = true, Padding = new Thickness(14, 6, 14, 6) };
        ok.Click += (_, _) =>
        {
            window.DialogResult = true;
            window.Close();
        };
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        panel.Children.Add(actions);
        window.Content = panel;

        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };

        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private static string Title(InstalledUserscript installed) =>
        string.IsNullOrWhiteSpace(installed.Script.Version)
            ? installed.Script.Name
            : $"{installed.Script.Name} {installed.Script.Version}";

    private static string Title(Userscript script) =>
        string.IsNullOrWhiteSpace(script.Version) ? script.Name : $"{script.Name} {script.Version}";

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0.0} MB";

    private void OnBack(object sender, RoutedEventArgs e) => _back();
}
