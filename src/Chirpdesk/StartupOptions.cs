namespace Chirpdesk;

/// <summary>
/// Command-line switches: the automated gate, the toast identity check, and the one manual
/// path phase 0 needs (show a warm service so a human can sign in).
/// </summary>
internal sealed record StartupOptions(bool SelfTest, bool TestToast, TimeSpan Soak, string? OpenService)
{
    private const string SoakPrefix = "--soak-seconds=";
    private const string OpenPrefix = "--open";

    public static StartupOptions Parse(string[] args)
    {
        var selfTest = Array.Exists(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        var testToast = Array.Exists(args, a => a.Equals("--test-toast", StringComparison.OrdinalIgnoreCase));

        var soak = TimeSpan.FromSeconds(60);
        string? open = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith(SoakPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg[SoakPrefix.Length..], out var seconds))
            {
                soak = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));
                continue;
            }

            // Bare --open means "the first configured service"; --open=whatsapp names one.
            if (arg.Equals(OpenPrefix, StringComparison.OrdinalIgnoreCase))
            {
                open = string.Empty;
            }
            else if (arg.StartsWith(OpenPrefix + "=", StringComparison.OrdinalIgnoreCase))
            {
                open = arg[(OpenPrefix.Length + 1)..].Trim();
            }
        }

        return new StartupOptions(selfTest, testToast, soak, open);
    }
}
