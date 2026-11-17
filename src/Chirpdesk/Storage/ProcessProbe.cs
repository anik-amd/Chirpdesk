using System.Diagnostics;

namespace Chirpdesk.Storage;

/// <summary>Never delete a known app's folder while that app is running.</summary>
internal static class ProcessProbe
{
    private static readonly Dictionary<string, string[]> ProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WhatsApp"] = ["WhatsApp", "WhatsAppDesktop"],
        ["Telegram"] = ["Telegram"],
        ["Ferdium"] = ["Ferdium"],
        ["Rambox"] = ["Rambox"],
        ["Station"] = ["Station"],
        ["MultiMessenger"] = ["MultiMessenger"],
    };

    public static bool IsRunning(string app)
    {
        if (!ProcessNames.TryGetValue(app, out var names))
        {
            return false;
        }

        foreach (var name in names)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Process list raced with exit; treat as not running.
            }
        }

        return false;
    }
}
