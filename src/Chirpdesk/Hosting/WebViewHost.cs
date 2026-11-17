using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Chirpdesk.Hosting;

/// <summary>
/// Creates the WebView2 environment for one profile.
/// The three flags are the reason a tray-hidden app can still keep a chat socket alive:
/// without them Chromium throttles timers and backgrounds occluded windows.
/// </summary>
internal static class WebViewHost
{
    public static readonly string[] AntiThrottleFlags =
    [
        "--disable-background-timer-throttling",
        "--disable-renderer-backgrounding",
        "--disable-backgrounding-occluded-windows",
    ];

    public static string BrowserVersion
    {
        get
        {
            try
            {
                return CoreWebView2Environment.GetAvailableBrowserVersionString() ?? string.Empty;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return string.Empty;
            }
        }
    }

    private static readonly Dictionary<string, Task<CoreWebView2Environment>> Environments =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// One environment per profile directory, cached for the life of the process. Creating a
    /// second one over a live folder is invalid (ADR 0005), so the cache is a correctness
    /// requirement, not an optimisation: two instances that share a profile must get the same
    /// object.
    /// </summary>
    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string profileId, string? profileDirectory = null)
    {
        var directory = Path.GetFullPath(profileDirectory ?? AppPaths.ProfileDirectory(profileId));
        Directory.CreateDirectory(directory);

        lock (Gate)
        {
            if (Environments.TryGetValue(directory, out var existing))
            {
                return existing;
            }

            var created = CreateAndTrackAsync(directory);
            Environments[directory] = created;
            return created;
        }
    }

    /// <summary>Drops a cached environment. Used by probes whose profile folder is deleted.</summary>
    public static void Forget(string profileDirectory)
    {
        var directory = Path.GetFullPath(profileDirectory);

        lock (Gate)
        {
            Environments.Remove(directory);
        }
    }

    private static async Task<CoreWebView2Environment> CreateAndTrackAsync(string directory)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', AntiThrottleFlags),
        };

        try
        {
            return await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: directory,
                options);
        }
        catch
        {
            // A failed environment must not be cached as a permanently broken entry.
            Forget(directory);
            throw;
        }
    }
}
