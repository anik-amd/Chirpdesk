namespace Chirpdesk.Core.Settings;

/// <summary>The whole user configuration. Small enough to rewrite atomically on every change.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 5;

    public const int DefaultCacheBudgetMb = 60;
    public const int DefaultGlobalCacheBudgetMb = 180;
    public const int MinCacheBudgetMb = 10;
    public const int MaxCacheBudgetMb = 2000;
    public const int MaxGlobalCacheBudgetMb = 20000;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>. One palette; no colour families yet.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Toast suppression until this instant. Null or past means off.</summary>
    public DateTimeOffset? DndUntil { get; set; }

    /// <summary>Mirror of the HKCU Run entry, so Settings can show the real state.</summary>
    public bool Autostart { get; set; }

    /// <summary>Opt-in background check of the signed stable release channel.</summary>
    public bool UpdateAutoCheck { get; set; }

    /// <summary>Minimize hides to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Close hides to the tray instead of quitting. True preserves the tray-app behavior.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary><c>always</c>, <c>hover</c> or <c>hidden</c> for the service rail.</summary>
    public string RailMode { get; set; } = "always";

    /// <summary>Flavour id for light mode; Latte in the Catppuccin pack.</summary>
    public string LightTheme { get; set; } = "latte";

    /// <summary>Flavour id for dark mode; Mocha in the Catppuccin pack.</summary>
    public string DarkTheme { get; set; } = "mocha";

    /// <summary>Cache budget per profile, in MB. Session data is never counted.</summary>
    public int CacheBudgetMb { get; set; } = DefaultCacheBudgetMb;

    /// <summary>Cache budget across every disposed profile, in MB.</summary>
    public int GlobalCacheBudgetMb { get; set; } = DefaultGlobalCacheBudgetMb;

    /// <summary>Running total of bytes reclaimed by trims; display only.</summary>
    public long ReclaimedBytes { get; set; }

    /// <summary>
    /// Non-service plugins the user turned off. Bundled plugins are active until listed here,
    /// so a new bundled plugin works after an upgrade without a migration step.
    /// </summary>
    public List<string> DisabledPlugins { get; set; } = [];

    public List<ServiceInstance> Services { get; set; } = [];

    /// <summary>Static HTTPS plugin repositories the user added.</summary>
    public List<RepositorySetting> Repositories { get; set; } = [];

    /// <summary>True once the bundled official repository has been offered; removal is respected.</summary>
    public bool OfficialRepositorySeeded { get; set; }

    /// <summary>Saved normal bounds in WPF DIPs; null means the window has no placement yet.</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>True when the last user-visible state was maximized; minimized restores as normal.</summary>
    public bool WindowMaximized { get; set; }
}
