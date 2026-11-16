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

    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string profileId, string? profileDirectory = null)
    {
        var directory = profileDirectory ?? AppPaths.ProfileDirectory(profileId);
        Directory.CreateDirectory(directory);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', AntiThrottleFlags),
        };

        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: directory, options);
    }
}
