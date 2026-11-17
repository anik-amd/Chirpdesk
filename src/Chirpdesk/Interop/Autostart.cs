using System.IO;
using System.Security;
using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Logging;
using Microsoft.Win32;

namespace Chirpdesk.Interop;

/// <summary>
/// The one HKCU Run value Chirpdesk owns. No service, no elevation, no Task Scheduler; the app
/// delays its own warm-up when launched with <c>--autostart</c> (ADR 0012).
/// </summary>
internal static class Autostart
{
    public static bool IsEnabled(string executable, ILog log)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutostartEntry.RunKeyPath);
            var value = key?.GetValue(AutostartEntry.ValueName) as string;
            return AutostartEntry.IsCurrent(value, executable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            log.Warn($"could not read the autostart entry: {ex.Message}");
            return false;
        }
    }

    public static bool TrySet(bool enabled, string executable, ILog log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(AutostartEntry.RunKeyPath);

            if (enabled)
            {
                key.SetValue(AutostartEntry.ValueName, AutostartEntry.Command(executable));
            }
            else
            {
                key.DeleteValue(AutostartEntry.ValueName, throwOnMissingValue: false);
            }

            log.Info($"autostart {(enabled ? "enabled" : "disabled")}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            log.Error($"could not update the autostart entry (enabled={enabled})", ex);
            return false;
        }
    }
}
