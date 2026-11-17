using Microsoft.Win32;

namespace Chirpdesk.Theming;

/// <summary>
/// The only OS-facing part of theming: Windows' "choose your default app mode" value, plus the
/// change notification so <c>system</c> follows the setting live. Fail open to light — a
/// wrong theme is annoying, an unreadable window is worse.
/// </summary>
internal static class SystemTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    public static event EventHandler? Changed;

    public static bool UsesLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(ValueName) is int value ? value != 0 : true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void Start()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General
                or UserPreferenceCategory.VisualStyle
                or UserPreferenceCategory.Color)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        };
    }
}
