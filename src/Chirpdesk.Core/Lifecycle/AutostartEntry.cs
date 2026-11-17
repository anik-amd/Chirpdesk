using System.IO;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Lifecycle;

/// <summary>
/// The HKCU Run value Chirpdesk owns. Formatting lives in Core so it is testable; writing the
/// registry stays in the app layer.
/// </summary>
public static class AutostartEntry
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Chirpdesk";
    public const string AutostartArgument = "--autostart";

    public static string Command(string executable) =>
        $"\"{Path.GetFullPath(executable)}\" {AutostartArgument}";

    public static bool IsCurrent(string? existing, string executable) =>
        !string.IsNullOrWhiteSpace(existing)
        && string.Equals(existing.Trim(), Command(executable), StringComparison.OrdinalIgnoreCase);
}
