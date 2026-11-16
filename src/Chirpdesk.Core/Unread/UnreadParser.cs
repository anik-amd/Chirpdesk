using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Unread;

/// <summary>
/// Reads an unread count out of a window title with a per-instance pattern.
/// Any failure returns 0: a missing badge is a cosmetic bug, an exception here would
/// kill the title-changed handler for the whole app.
/// </summary>
public static class UnreadParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static int Parse(string? title, string? pattern)
    {
        if (string.IsNullOrEmpty(title) || string.IsNullOrWhiteSpace(pattern))
        {
            return 0;
        }

        Match match;
        try
        {
            match = Regex.Match(title, pattern, RegexOptions.None, MatchTimeout);
        }
        catch (ArgumentException)
        {
            // Malformed pattern from settings or a plugin definition: fail open (no badge).
            return 0;
        }
        catch (RegexMatchTimeoutException)
        {
            // Catastrophic backtracking on a hostile title: give up, keep the app alive.
            return 0;
        }

        if (!match.Success || match.Groups.Count < 2)
        {
            return 0;
        }

        return int.TryParse(match.Groups[1].Value, out var count) && count > 0 ? count : 0;
    }
}
