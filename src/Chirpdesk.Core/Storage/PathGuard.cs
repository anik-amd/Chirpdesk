using System.IO;

namespace Chirpdesk.Core.Storage;

/// <summary>
/// Every delete path goes through here. A path is allowed only when it resolves inside the
/// fixed root, so `..`, sibling prefixes and absolute paths from settings cannot escape.
/// </summary>
public static class PathGuard
{
    public static bool IsUnder(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root)
            || string.IsNullOrWhiteSpace(candidate)
            || !Path.IsPathFullyQualified(root)
            || !Path.IsPathFullyQualified(candidate))
        {
            return false;
        }

        string fullRoot;
        string fullCandidate;

        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
