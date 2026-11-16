namespace Chirpdesk;

/// <summary>Command-line switches. Everything the automated gate needs, and nothing more.</summary>
internal sealed record StartupOptions(bool SelfTest, bool TestToast, TimeSpan Soak)
{
    private const string SoakPrefix = "--soak-seconds=";

    public static StartupOptions Parse(string[] args)
    {
        var selfTest = Array.Exists(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        var testToast = Array.Exists(args, a => a.Equals("--test-toast", StringComparison.OrdinalIgnoreCase));

        var soak = TimeSpan.FromSeconds(60);
        foreach (var arg in args)
        {
            if (arg.StartsWith(SoakPrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg[SoakPrefix.Length..], out var seconds))
            {
                soak = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600));
            }
        }

        return new StartupOptions(selfTest, testToast, soak);
    }
}
