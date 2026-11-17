using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Blocking;

public enum RequestVerdict
{
    Allow,
    Block,
}

/// <summary>
/// Compiled request matcher for one service instance. Policy is fail-open: anything the matcher
/// cannot parse, any exception, and anything on the permanent allowlist is allowed. The
/// allowlist is checked before the block tiers, so no pack can override auth, WebSocket or
/// update traffic (ADR 0004).
/// </summary>
public sealed partial class RequestMatcher
{
    private const int CacheLimit = 8192;

    /// <summary>Hosts that must never be blocked, whatever a pack says.</summary>
    private static readonly string[] PermanentAllowHosts =
    [
        "accounts.google.com",
        "login.microsoftonline.com",
        "appleid.apple.com",
        "web.whatsapp.com",
        "*.whatsapp.net",
        "web.telegram.org",
        "*.telegram.org",
    ];

    /// <summary>Path fragments used by login, OAuth, WebSocket and account recovery flows.</summary>
    private static readonly string[] PermanentAllowPaths =
    [
        "*/ws",
        "*/ws/*",
        "*/websocket*",
        "*/login*",
        "*/signin*",
        "*/oauth*",
        "*/accounts*",
        "*/auth/*",
        "*/checkpoint*",
    ];

    [GeneratedRegex(
        "^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HostName();

    private readonly List<HostRule> _allowHosts = [];
    private readonly List<HostRule> _blockHosts = [];
    private readonly List<Regex> _allowPaths = [];
    private readonly List<Regex> _blockPaths = [];
    private readonly Dictionary<string, bool> _cache = new(StringComparer.Ordinal);
    private readonly string _serviceHost;

    public RequestMatcher(string serviceOrigin, IEnumerable<FilterPack> packs)
    {
        ArgumentNullException.ThrowIfNull(packs);

        _serviceHost = NormalizeHost(OriginHost(serviceOrigin));

        foreach (var pattern in PermanentAllowHosts)
        {
            AddHost(_allowHosts, pattern);
        }

        foreach (var pattern in PermanentAllowPaths)
        {
            AddPath(_allowPaths, pattern);
        }

        var cosmetic = new List<string>();

        foreach (var pack in packs)
        {
            if (pack is null)
            {
                continue;
            }

            foreach (var pattern in pack.AllowHosts ?? [])
            {
                AddHost(_allowHosts, pattern);
            }

            foreach (var pattern in pack.BlockHosts ?? [])
            {
                AddHost(_blockHosts, pattern);
            }

            foreach (var pattern in pack.BlockPaths ?? [])
            {
                AddPath(_blockPaths, pattern);
            }

            foreach (var selector in pack.Cosmetic ?? [])
            {
                var trimmed = selector?.Trim();
                if (!string.IsNullOrEmpty(trimmed)
                    && !cosmetic.Contains(trimmed, StringComparer.Ordinal))
                {
                    cosmetic.Add(trimmed);
                }
            }
        }

        CosmeticSelectors = cosmetic;
    }

    /// <summary>Rules that could not be compiled. Shown in diagnostics; never fatal.</summary>
    public int SkippedRules { get; private set; }

    /// <summary>Conservative tier-2 selectors, injected as CSS by the host.</summary>
    public IReadOnlyList<string> CosmeticSelectors { get; }

    public RequestVerdict Evaluate(string? uri)
    {
        try
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                return RequestVerdict.Allow;
            }

            // WebSocket handshakes and non-network schemes are never blocked.
            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                return RequestVerdict.Allow;
            }

            var host = NormalizeHost(parsed.Host);
            var path = parsed.PathAndQuery;
            var key = host + "\n" + path;

            if (_cache.TryGetValue(key, out var cached))
            {
                return cached ? RequestVerdict.Block : RequestVerdict.Allow;
            }

            var verdict = EvaluateCore(host, path);

            if (_cache.Count >= CacheLimit)
            {
                _cache.Clear();
            }

            _cache[key] = verdict == RequestVerdict.Block;
            return verdict;
        }
        catch
        {
            return RequestVerdict.Allow;
        }
    }

    private RequestVerdict EvaluateCore(string host, string path)
    {
        // The service's own origin is allowlisted before any pack is consulted.
        if (host.Length > 0 && string.Equals(host, _serviceHost, StringComparison.Ordinal))
        {
            return RequestVerdict.Allow;
        }

        if (MatchesAny(_allowHosts, host) || MatchesAny(_allowPaths, path))
        {
            return RequestVerdict.Allow;
        }

        return MatchesAny(_blockHosts, host) || MatchesAny(_blockPaths, path)
            ? RequestVerdict.Block
            : RequestVerdict.Allow;
    }

    private static bool MatchesAny(List<HostRule> rules, string host)
    {
        foreach (var rule in rules)
        {
            if (rule.Matches(host))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAny(List<Regex> rules, string path)
    {
        foreach (var rule in rules)
        {
            try
            {
                if (rule.IsMatch(path))
                {
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Ambiguous rule: fail open.
                return false;
            }
        }

        return false;
    }

    private void AddHost(List<HostRule> rules, string? pattern)
    {
        var normalized = pattern?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            SkippedRules++;
            return;
        }

        if (normalized.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = normalized[2..];
            if (!IsHostName(suffix))
            {
                SkippedRules++;
                return;
            }

            rules.Add(new HostRule(null, suffix));
            return;
        }

        if (!IsHostName(normalized))
        {
            SkippedRules++;
            return;
        }

        rules.Add(new HostRule(normalized, null));
    }

    private void AddPath(List<Regex> rules, string? pattern)
    {
        var trimmed = pattern?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            SkippedRules++;
            return;
        }

        try
        {
            var glob = Regex.Escape(trimmed).Replace("\\*", ".*", StringComparison.Ordinal);
            rules.Add(new Regex(
                glob,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50)));
        }
        catch (ArgumentException)
        {
            SkippedRules++;
        }
    }

    private static bool IsHostName(string value) => HostName().IsMatch(value);

    private static string NormalizeHost(string? host) =>
        host?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;

    private static string OriginHost(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed))
        {
            return string.Empty;
        }

        return parsed.Host;
    }

    private sealed class HostRule(string? exact, string? suffix)
    {
        public bool Matches(string host) => exact is not null
            ? string.Equals(host, exact, StringComparison.Ordinal)
            : string.Equals(host, suffix, StringComparison.Ordinal)
                || host.EndsWith("." + suffix, StringComparison.Ordinal);
    }
}
