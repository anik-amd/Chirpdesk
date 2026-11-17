using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Settings;
using Chirpdesk.Core.Storage;
using Chirpdesk.Hosting;
using Chirpdesk.Interop;
using Chirpdesk.Storage;

namespace Chirpdesk.Settings;

/// <summary>
/// The only settings surface: services/instances, add-from-definition, appearance, autostart,
/// DND and storage. Changes save immediately; no tabs or rail.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] DndLabels = ["Off", "For 30 minutes", "For 1 hour", "Until tomorrow 08:00"];

    private readonly SettingsStore _store;
    private readonly ServiceCatalog _catalog;
    private readonly ServiceRegistry _registry;
    private readonly ILog _log;
    private readonly Func<ServiceInstance, Task> _open;
    private readonly Func<string, Task> _sleep;
    private readonly Action _applyTheme;
    private AppSettings _settings;
    private IReadOnlyList<OrphanItem> _orphans = [];
    private bool _loading;

    internal SettingsWindow(
        SettingsStore store,
        ServiceCatalog catalog,
        ServiceRegistry registry,
        ILog log,
        Func<ServiceInstance, Task> open,
        Func<string, Task> sleep,
        Action applyTheme,
        bool dark)
    {
        InitializeComponent();
        _store = store;
        _catalog = catalog;
        _registry = registry;
        _log = log;
        _open = open;
        _sleep = sleep;
        _applyTheme = applyTheme;
        _settings = store.Load();

        ThemeBox.ItemsSource = new[] { "system", "light", "dark" };
        DndBox.ItemsSource = DndLabels;
        DefinitionBox.ItemsSource = catalog.Definitions.OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase).ToList();
        DefinitionBox.DisplayMemberPath = nameof(ServiceDefinition.Name);

        SetDark(dark);
        LoadControls();
        RefreshServices();
        RefreshStorage();
    }

    public void SetDark(bool dark)
    {
        Background = new SolidColorBrush(dark ? Color.FromRgb(0x11, 0x12, 0x14) : Color.FromRgb(0xF6, 0xF5, 0xF3));
        TextElement.SetForeground(
            Root,
            new SolidColorBrush(dark ? Color.FromRgb(0xC9, 0xC4, 0xCC) : Color.FromRgb(0x33, 0x30, 0x36)));
    }

    private void LoadControls()
    {
        _loading = true;
        ThemeBox.SelectedItem = _settings.Theme;
        AutostartBox.IsChecked = _settings.Autostart;

        var dndIndex = 0;
        if (_settings.DndUntil is { } dnd && dnd > DateTimeOffset.UtcNow)
        {
            var minutes = (dnd - DateTimeOffset.UtcNow).TotalMinutes;
            dndIndex = minutes <= 31 ? 1 : minutes <= 61 ? 2 : 3;
        }

        DndBox.SelectedIndex = dndIndex;
        _loading = false;
    }

    private void RefreshServices()
    {
        ServicesPanel.Children.Clear();

        foreach (var instance in _settings.Services.OrderBy(service => service.Order))
        {
            var state = _registry.StateOf(instance.InstanceId);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { Orientation = Orientation.Vertical };
            label.Children.Add(new TextBlock { Text = instance.Name, FontSize = 13 });
            label.Children.Add(new TextBlock
            {
                Text = $"{instance.DefinitionId ?? "legacy"} · {state.ToString().ToLowerInvariant()}",
                FontSize = 11,
                Opacity = 0.65,
            });
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var warm = new CheckBox
            {
                Content = "Warm at boot",
                IsChecked = instance.WarmAtBoot,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
            };
            warm.Checked += (_, _) => SaveInstance(instance, i => i.WarmAtBoot = true);
            warm.Unchecked += (_, _) => SaveInstance(instance, i => i.WarmAtBoot = false);
            Grid.SetColumn(warm, 1);
            row.Children.Add(warm);

            var mute = new CheckBox
            {
                Content = "Mute",
                IsChecked = instance.Mute,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            mute.Checked += (_, _) => SaveInstance(instance, i => i.Mute = true);
            mute.Unchecked += (_, _) => SaveInstance(instance, i => i.Mute = false);
            Grid.SetColumn(mute, 2);
            row.Children.Add(mute);

            var toggle = new Button
            {
                Content = state == RuntimeState.Warm ? "Sleep" : "Open",
                Width = 72,
                Margin = new Thickness(0, 0, 8, 0),
            };
            toggle.Click += async (_, _) => await ToggleRuntimeAsync(instance);
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            var remove = new Button { Content = "Remove", Width = 76 };
            remove.Click += async (_, _) => await RemoveInstanceAsync(instance);
            Grid.SetColumn(remove, 4);
            row.Children.Add(remove);

            ServicesPanel.Children.Add(row);
        }

        if (_settings.Services.Count == 0)
        {
            ServicesPanel.Children.Add(new TextBlock { Text = "No services yet. Add one from a definition below.", Opacity = 0.7 });
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

    private async Task ToggleRuntimeAsync(ServiceInstance instance)
    {
        try
        {
            if (_registry.StateOf(instance.InstanceId) == RuntimeState.Warm)
            {
                await _sleep(instance.InstanceId);
            }
            else
            {
                await _open(instance);
            }

            RefreshServices();
            RefreshStorage();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidSettingsException)
        {
            _log.Error($"settings: could not change runtime state for '{instance.InstanceId}'", ex);
            MessageBox.Show(this, ex.Message, "Chirpdesk", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RemoveInstanceAsync(ServiceInstance instance)
    {
        var answer = MessageBox.Show(
            this,
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
            await _sleep(instance.InstanceId);
        }

        _settings.Services.Remove(instance);
        Save();
        RefreshServices();
        RefreshStorage();
    }

    private void OnAddService(object sender, RoutedEventArgs e)
    {
        if (DefinitionBox.SelectedItem is not ServiceDefinition definition)
        {
            MessageBox.Show(this, "Pick a definition first.", "Add service", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var instance = InstanceFactory.Create(definition, NameBox.Text, _settings.Services);
            _settings.Services.Add(instance);
            Save();
            NameBox.Text = string.Empty;
            RefreshServices();
            _log.Info($"settings: created instance '{instance.InstanceId}' from '{definition.Id}'");
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(this, ex.Message, "Add service", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedItem is not string theme)
        {
            return;
        }

        _settings.Theme = theme;
        Save();
        _applyTheme();
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
            MessageBox.Show(this, "Windows refused the autostart change.", "Autostart", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Autostart = enabled;
        Save();
    }

    private void OnDndChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.DndUntil = DndBox.SelectedIndex switch
        {
            1 => DateTimeOffset.UtcNow.AddMinutes(30),
            2 => DateTimeOffset.UtcNow.AddHours(1),
            3 => TomorrowMorning(),
            _ => null,
        };

        Save();
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
                MessageBox.Show(this, "Nothing to trim: profiles are under budget or still warm.",
                    "Storage", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            RefreshStorage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("settings: trim failed", ex);
            MessageBox.Show(this, ex.Message, "Storage", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                if (MessageBox.Show(this, $"Move '{orphan.Path}' to the Recycle Bin?", "Remove leftover",
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

        _log.Info($"settings: orphan scan found {_orphans.Count} item(s)");
        RefreshOrphans();
    }

    private void Save()
    {
        _store.Save(_settings);
    }

    private static string Megabytes(long bytes) => $"{bytes / (1024d * 1024d):0.0} MB";

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
