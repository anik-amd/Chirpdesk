namespace Chirpdesk;

/// <summary>
/// Command-line switches: the automated gate, the toast identity check, the one manual path
/// phase 0 needs (show a warm service so a human can sign in), and a timed quit that lets the
/// harness prove the process tree actually goes away.
/// </summary>
internal sealed record StartupOptions(
    bool SelfTest,
    bool TestToast,
    TimeSpan Soak,
    string? OpenService,
    TimeSpan? QuitAfter,
    bool Hidden)
{
    private const string SoakPrefix = "--soak-seconds=";
    private const string QuitPrefix = "--quit-after=";
    private const string OpenPrefix = "--open";

    public static StartupOptions Parse(string[] args)
    {
        var selfTest = Array.Exists(args, a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase));
        var testToast = Array.Exists(args, a => a.Equals("--test-toast", StringComparison.OrdinalIgnoreCase));
        var hidden = Array.Exists(args, a => a.Equals("--hidden", StringComparison.OrdinalIgnoreCase));

        var soak = TimeSpan.FromSeconds(60);
        TimeSpan? quitAfter = null;
        string? open = null;

        foreach (var arg in args)
        {
            if (TryReadSeconds(arg, SoakPrefix, out var seconds))
            {
                soak = TimeSpan.FromSeconds(seconds);
                continue;
            }

            if (TryReadSeconds(arg, QuitPrefix, out var quitSeconds))
            {
                quitAfter = TimeSpan.FromSeconds(quitSeconds);
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

        return new StartupOptions(selfTest, testToast, soak, open, quitAfter, hidden);
    }

    /// <summary>Any timeout is clamped to something a human would accept (10 s to 1 h).</summary>
    private static bool TryReadSeconds(string arg, string prefix, out int seconds)
    {
        seconds = 0;
        return arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(arg[prefix.Length..], out var raw)
            && (seconds = Math.Clamp(raw, 10, 3600)) > 0;
    }
}
