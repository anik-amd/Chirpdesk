using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Userscripts;

/// <summary>
/// Reads a <c>.user.js</c> file. The metadata block is the Tampermonkey format: a
/// <c>// ==UserScript==</c> header with <c>@key value</c> lines. Unknown keys are kept out of
/// the model instead of rejected, so a script written for a newer TM still installs; the host
/// only acts on the keys it understands.
/// </summary>
public static class UserscriptParser
{
    private const int MaxMetadataLines = 200;

    public static Userscript Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new FormatException("The script is empty.");
        }

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var name = (string?)null;
        string? ns = null, version = null, description = null, runAt = null, update = null, download = null;
        var matches = new List<string>();
        var includes = new List<string>();
        var grants = new List<string>();

        var inHeader = false;
        var headerEnd = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!inHeader)
            {
                if (line == "// ==UserScript==")
                {
                    inHeader = true;
                }
                else if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
                {
                    // No metadata block: still install, but the script matches nothing.
                    break;
                }

                continue;
            }

            if (line == "// ==/UserScript==")
            {
                headerEnd = i;
                break;
            }

            if (i > MaxMetadataLines)
            {
                throw new FormatException("The metadata block is too long.");
            }

            if (!line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            var content = line[2..].Trim();
            if (!content.StartsWith('@'))
            {
                continue;
            }

            var space = content.IndexOfAny([' ', '\t']);
            if (space <= 1)
            {
                continue;
            }

            var key = content[1..space];
            var value = content[(space + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "name":
                    name ??= value;
                    break;
                case "namespace":
                    ns ??= value;
                    break;
                case "version":
                    version ??= value;
                    break;
                case "description":
                    description ??= value;
                    break;
                case "match":
                    matches.Add(value);
                    break;
                case "include":
                    includes.Add(value);
                    break;
                case "grant":
                    grants.Add(value);
                    break;
                case "run-at":
                    runAt ??= value;
                    break;
                case "updateurl":
                    update ??= value;
                    break;
                case "downloadurl":
                    download ??= value;
                    break;
            }
        }

        var body = headerEnd >= 0
            ? string.Join('\n', lines[(headerEnd + 1)..])
            : source;

        return new Userscript
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled script" : name,
            Namespace = ns,
            Version = version,
            Description = description,
            Matches = matches,
            Includes = includes,
            Grants = grants,
            RunAt = string.Equals(runAt, "document-start", StringComparison.OrdinalIgnoreCase)
                ? "document-start"
                : "document-idle",
            UpdateUrl = update,
            DownloadUrl = download,
            Body = body,
        };
    }
}

/// <summary>
/// Tampermonkey/Chrome match patterns. Supported: <c>*</c> or a scheme, a literal host or
/// <c>*.host</c> (that host and its subdomains), and a path with <c>*</c> wildcards.
/// <c>@include</c> is a plain glob over the whole URL, plus <c>/regex/</c> in slashes.
/// </summary>
public static class UserscriptMatcher
{
    public static bool Match(string pattern, string url)
    {
        if (string.IsNullOrWhiteSpace(pattern) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var separator = pattern.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var scheme = pattern[..separator];
        var rest = pattern[(separator + 3)..];
        var slash = rest.IndexOf('/');
        var host = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "/" : rest[slash..];

        if (scheme != "*" && !string.Equals(scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!HostMatches(host, uri))
        {
            return false;
        }

        var actual = uri.AbsolutePath + uri.Query;
        return GlobMatches(path, actual);
    }

    /// <summary>@include: a glob over the whole URL, or a slash-delimited regular expression.</summary>
    public static bool Include(string pattern, string url)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
        {
            try
            {
                return Regex.IsMatch(url, pattern[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return GlobMatches(pattern, url);
    }

    private static bool HostMatches(string host, Uri uri)
    {
        if (host == "*")
        {
            return true;
        }

        var actual = uri.Host;
        if (host.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = host[2..];
            return actual.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || actual.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
        }

        return actual.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
        try
        {
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
