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

    public static string LogsRoot => Path.Combine(LocalRoot, "logs");

    /// <summary>Written by <c>--selftest</c>; the exit code is still the verdict, this is the detail.</summary>
    public static string SelfTestReport => Path.Combine(LogsRoot, "selftest.log");

    public static string ProfileDirectory(string profileId) => Path.Combine(ProfilesRoot, profileId);
}
