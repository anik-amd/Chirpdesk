using System.IO;
namespace Chirpdesk;

/// <summary>Every path the app writes to, in one place, all derived from the OS folders.</summary>
public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chirpdesk");

    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chirpdesk");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    public static string ProfilesRoot => Path.Combine(LocalRoot, "profiles");

    /// <summary>Verified installers waiting to be applied. Safe to clear on boot.</summary>
    public static string UpdatesRoot => Path.Combine(DataRoot, "updates");

    /// <summary>Signed repository index cache. Safe to clear at any time.</summary>
    public static string RepositoryCacheRoot => Path.Combine(DataRoot, "repositories");

    /// <summary>Previous repository-installed plugin versions, outside the live plugin scan root.</summary>
    public static string PluginBackupsRoot => Path.Combine(DataRoot, "plugin-backups");

    public static string LogsRoot => Path.Combine(LocalRoot, "logs");

    /// <summary>Bundled plugins that travel beside the executable. Trusted by install location.</summary>
    public static string BundledPluginsDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    /// <summary>User-installed plugins. Roaming, because they are content, not machine state.</summary>
    public static string UserPluginsDirectory { get; } = Path.Combine(DataRoot, "plugins");

    /// <summary>Written by <c>--selftest</c>; the exit code is still the verdict, this is the detail.</summary>
    public static string SelfTestReport => Path.Combine(LogsRoot, "selftest.log");

    public static string ProfileDirectory(string profileId) => Path.Combine(ProfilesRoot, profileId);
}
