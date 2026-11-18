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

namespace Chirpdesk.Settings;

/// <summary>
/// The only settings surface: services/instances, add-from-definition, appearance, autostart,
/// DND and storage. Changes save immediately; no tabs or rail.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly SettingsStore _store;
    private readonly ServiceCatalog _catalog;
    private readonly IReadOnlyList<PanelDefinition> _panels;
    private readonly ServiceRegistry _registry;
    private readonly HostWindow _host;
    private readonly IReadOnlyList<ThemeFlavor> _themeFlavors;
    private readonly ILog _log;
    private readonly Action _applyTheme;
    private readonly Action? _stateChanged;
    private readonly Action _back;
    private AppSettings _settings;
    private IReadOnlyList<OrphanItem> _orphans = [];
    private bool _scanned;
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
        Action back)
    {
        InitializeComponent();
        _store = store;
        _catalog = catalog;
        _panels = panels;
        _registry = registry;
        _host = host;
        _themeFlavors = themeFlavors;
        _log = log;
        _applyTheme = applyTheme;
        _stateChanged = stateChanged;
        _back = back;
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
        RefreshServices();
        RenderToolPanel();
        RefreshStorage();
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

    private void RefreshServices()
    {
        ServicesPanel.Children.Clear();

        var ordered = _settings.Services.OrderBy(service => service.Order).ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            var instance = ordered[index];
            var state = _registry.StateOf(instance.InstanceId);

            var block = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = ServiceIcons.Create(
                instance.DefinitionId,
                instance.Name,
                _catalog?.Find(instance.DefinitionId)?.Accent);
            Grid.SetColumn(badge, 0);
            header.Children.Add(badge);

            var names = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(12, 0, 12, 0) };
            names.Children.Add(new TextBlock
            {
                Text = instance.Name,
                FontFamily = (FontFamily)FindResource("CdBodyFont"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Token("CdText"),
            });

            var blockingOff = string.Equals(instance.Blocking, "off", StringComparison.OrdinalIgnoreCase);
            var blocked = blockingOff ? 0 : _registry.Find(instance.InstanceId)?.BlockedRequests ?? 0;
            var blockingText = blockingOff
                ? "blocking off"
                : blocked > 0
                    ? $"blocking on · {blocked} blocked"
                    : "blocking on";

            names.Children.Add(new TextBlock
            {
                Text = $"{state.ToString().ToLowerInvariant()} · {blockingText} · {instance.DefinitionId ?? "legacy"}",
                FontFamily = (FontFamily)FindResource("CdBodyFont"),
                FontSize = 11,
                Foreground = Token("CdTextMuted"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            Grid.SetColumn(names, 1);
            header.Children.Add(names);

            var remove = new Button
            {
                Content = "Remove",
                Width = 76,
                Style = (Style)FindResource("GhostButton"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            remove.Click += async (_, _) => await RemoveInstanceAsync(instance);
            Grid.SetColumn(remove, 2);
            header.Children.Add(remove);

            block.Children.Add(header);

            var actions = new WrapPanel { Margin = new Thickness(34, 6, 0, 0) };
            var warm = new CheckBox
            {
                Content = "Warm at boot",
                IsChecked = instance.WarmAtBoot,
                Style = (Style)FindResource("CdSwitch"),
                Margin = new Thickness(0, 0, 18, 0),
            };
            warm.Checked += (_, _) => SaveInstance(instance, i => i.WarmAtBoot = true);
            warm.Unchecked += (_, _) => SaveInstance(instance, i => i.WarmAtBoot = false);
            actions.Children.Add(warm);

            var mute = new CheckBox
            {
                Content = "Mute",
                IsChecked = instance.Mute,
                Style = (Style)FindResource("CdSwitch"),
            };
            mute.Checked += (_, _) => SaveInstance(instance, i => i.Mute = true);
            mute.Unchecked += (_, _) => SaveInstance(instance, i => i.Mute = false);
            actions.Children.Add(mute);

            block.Children.Add(actions);
            ServicesPanel.Children.Add(block);

            if (index < ordered.Count - 1)
            {
                ServicesPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = Token("CdRule"),
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }

        if (_settings.Services.Count == 0)
        {
            ServicesPanel.Children.Add(new TextBlock
            {
                Text = "No services yet. Open Plugins & services from Ctrl+K and activate one.",
                Foreground = Token("CdTextMuted"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            });
        }
    }

    private void SaveInstance(ServiceInstance instance, Action<ServiceInstance> change)
    {
        if (_loading)
        {
            return;
        }

        change(instance);
        Save();
        RefreshServices();
    }

    private async Task RemoveInstanceAsync(ServiceInstance instance)
    {
        var answer = MessageBox.Show(
            $"Remove '{instance.Name}' from the list?\n\nThe profile stays on disk so it can be removed from Storage.",
            "Remove service",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (_registry.StateOf(instance.InstanceId) == RuntimeState.Warm)
        {
            await _registry.SleepAsync(instance.InstanceId);
        }

        _settings.Services.Remove(instance);
        Save();
        RefreshServices();
        RefreshStorage();
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

    private void OnTrimAll(object sender, RoutedEventArgs e)
    {
        try
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
                Save();
            }
            else
            {
                MessageBox.Show("Nothing to trim: profiles are under budget or still warm.",
                    "Storage", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            RefreshStorage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("settings: trim failed", ex);
            MessageBox.Show(ex.Message, "Storage", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

    private void RefreshStorage()
    {
        StoragePanel.Children.Clear();

        var usage = ProfileScanner.ScanAll();
        var reclaimedMb = _settings.ReclaimedBytes / (1024d * 1024d);
        ReclaimedText.Text = $"Reclaimed {reclaimedMb:0.0} MB since install";

        foreach (var profile in usage)
        {
            var names = _settings.Services
                .Where(instance => string.Equals(instance.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Select(instance => instance.Name)
                .ToList();

            var warm = _settings.Services
                .Where(instance => string.Equals(instance.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase))
                .Any(instance => _registry.StateOf(instance.InstanceId) == RuntimeState.Warm);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = names.Count > 0 ? string.Join(", ", names) : profile.ProfileId,
                Width = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"Data {Megabytes(profile.DataBytes)} · Cache {Megabytes(profile.CacheBytes)}",
                Width = 260,
                Opacity = 0.8,
            });
            row.Children.Add(new TextBlock
            {
                Text = warm ? "warm" : "disposed",
                Width = 70,
                Opacity = 0.6,
            });
            StoragePanel.Children.Add(row);
        }

        if (usage.Count == 0)
        {
            StoragePanel.Children.Add(new TextBlock { Text = "No profiles yet.", Opacity = 0.7 });
        }

        RefreshOrphans();
    }

    private void RefreshOrphans()
    {
        OrphanPanel.Children.Clear();

        OrphanStatus.Text = !_scanned
            ? "Not scanned yet."
            : _orphans.Count == 0
                ? "Scan complete: no leftovers found."
                : $"{_orphans.Count} leftover(s) found. Nothing is pre-selected.";

        if (_orphans.Count == 0)
        {
            return;
        }

        OrphanPanel.Children.Add(new TextBlock
        {
            Text = "Leftovers found (nothing is pre-selected)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
        });

        foreach (var orphan in _orphans)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock
            {
                Text = orphan.ProfileId ?? Path.GetFileName(orphan.Path),
                Width = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock { Text = orphan.App, Width = 150, Opacity = 0.8 });
            row.Children.Add(new TextBlock { Text = Megabytes(orphan.Bytes), Width = 90, Opacity = 0.8 });

            var recycle = new Button { Content = "Recycle", Width = 80 };
            recycle.Click += (_, _) =>
            {
                if (MessageBox.Show($"Move '{orphan.Path}' to the Recycle Bin?", "Remove leftover",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                {
                    return;
                }

                if (OrphanDeleter.Delete(orphan, _log))
                {
                    _orphans = _orphans.Where(item => item != orphan).ToList();
                    RefreshOrphans();
                }
            };
            row.Children.Add(recycle);
            OrphanPanel.Children.Add(row);
        }
    }

    private void OnScanOrphans(object sender, RoutedEventArgs e)
    {
        var referenced = _settings.Services
            .Select(instance => instance.ProfileId ?? instance.InstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _orphans = OrphanScanner.Scan(
            AppPaths.ProfilesRoot,
            referenced,
            KnownRoots.ForThisUser(),
            KnownRoots.IsInUse);

        _scanned = true;
        _log.Info($"settings: orphan scan found {_orphans.Count} item(s)");
        RefreshOrphans();
    }

    private void RenderToolPanel()
    {
        PanelHost.Children.Clear();

        var panel = _panels.Count > 0 ? _panels[0] : null;
        if (panel is null)
        {
            return;
        }

        PanelHost.Children.Add(new TextBlock
        {
            Text = panel.Title,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });

        if (!string.IsNullOrWhiteSpace(panel.Subtitle))
        {
            PanelHost.Children.Add(new TextBlock
            {
                Text = panel.Subtitle,
                FontSize = 12,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        foreach (var section in panel.Sections.Where(section => section.Type == "text"))
        {
            PanelHost.Children.Add(new TextBlock
            {
                Text = section.Text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.8,
                Margin = new Thickness(0, 2, 0, 6),
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        foreach (var action in panel.Actions)
        {
            var button = new Button
            {
                Content = action.Label,
                Width = 140,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = action.Confirm,
                Style = (Style)FindResource(action.Style == "primary" ? "PillButton" : "GhostButton"),
            };

            button.Click += (_, _) => ExecutePanelAction(action);
            buttons.Children.Add(button);
        }

        PanelHost.Children.Add(buttons);
    }

    private void ExecutePanelAction(PanelAction action)
    {
        switch (action.Primitive)
        {
            case "storageReport":
                RefreshStorage();
                return;

            case "trimCache":
                OnTrimAll(this, new RoutedEventArgs());
                return;

            case "scanOrphans":
                OnScanOrphans(this, new RoutedEventArgs());
                return;

            case "revealFolder":
                RevealRoot(action.Args?.Root);
                return;
        }
    }

    private void RevealRoot(string? root)
    {
        var path = root switch
        {
            "profiles" => AppPaths.ProfilesRoot,
            "plugins" => AppPaths.BundledPluginsDirectory,
            "logs" => AppPaths.LogsRoot,
            "data" => AppPaths.DataRoot,
            _ => null,
        };

        if (path is null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Warn($"could not open the {root} folder: {ex.Message}");
        }
    }

    private void Save()
    {
        _store.Save(_settings);
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0.0} MB";

    private void OnBack(object sender, RoutedEventArgs e) => _back();
}
