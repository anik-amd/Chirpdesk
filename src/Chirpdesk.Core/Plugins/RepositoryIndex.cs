using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Plugins;

public sealed class RepositoryIndex
{
    public int Schema { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset Generated { get; set; }

    public string KeyFingerprint { get; set; } = string.Empty;

    public List<RepositoryPlugin> Plugins { get; set; } = [];
}

public sealed class RepositoryPlugin
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int ApiVersion { get; set; }

    public string? MinAppVersion { get; set; }

    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    public string Url { get; set; } = string.Empty;

    public List<string>? Permissions { get; set; } = [];
}

/// <summary>
/// Reads the signed catalogue. The signature is checked over the exact bytes fetched, before
/// this parser creates any install plan; a malformed entry is rejected here rather than at the
/// filesystem boundary.
/// </summary>
public static partial class RepositoryIndexCodec
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "service", "theme", "filter", "tool", "integration",
    };

    public static RepositoryIndex? TryRead(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "repository index is empty";
            return null;
        }

        RepositoryIndex? index;

        try
        {
            index = JsonSerializer.Deserialize<RepositoryIndex>(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"repository index is not valid json: {ex.Message}";
            return null;
        }

        if (index is null)
        {
            error = "repository index is null";
            return null;
        }

        if (index.Schema != CurrentSchema)
        {
            error = $"repository schema {index.Schema} is not supported";
            return null;
        }

        if (string.IsNullOrWhiteSpace(index.Name))
        {
            error = "repository has no name";
            return null;
        }

        if (string.IsNullOrWhiteSpace(index.KeyFingerprint))
        {
            error = "repository has no keyFingerprint";
            return null;
        }

        index.Plugins ??= [];
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var plugin in index.Plugins)
        {
            var pluginError = ValidatePlugin(plugin);

            if (pluginError is not null)
            {
                error = $"{plugin.Id}: {pluginError}";
                return null;
            }

            if (!ids.Add(plugin.Id))
            {
                error = $"duplicate plugin id '{plugin.Id}'";
                return null;
            }
        }

        return index;
    }

    private static string? ValidatePlugin(RepositoryPlugin? plugin)
    {
        if (plugin is null)
        {
            return "entry is null";
        }

        if (!IdPattern().IsMatch(plugin.Id ?? string.Empty))
        {
            return "invalid id";
        }

        if (!Kinds.Contains(plugin.Kind ?? string.Empty))
        {
            return "unknown kind";
        }

        if (string.IsNullOrWhiteSpace(plugin.Name) || string.IsNullOrWhiteSpace(plugin.Version))
        {
            return "missing name or version";
        }

        if (plugin.ApiVersion <= 0)
        {
            return "missing apiVersion";
        }

        if (plugin.MinAppVersion is not null && !Version.TryParse(plugin.MinAppVersion, out _))
        {
            return "minAppVersion is not a numeric version";
        }

        if (!UpdateHex(plugin.Sha256))
        {
            return "sha256 is not 64 hex characters";
        }

        if (plugin.Size <= 0 || plugin.Size > PluginArchive.PolicyForKind(plugin.Kind).MaxBytes)
        {
            return "size is outside the archive limit";
        }

        if (!IsSafeRelativeUrl(plugin.Url))
        {
            return "url must be a safe relative path or absolute https URL";
        }

        return null;
    }

    public static bool IsSafeRelativeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            return string.Equals(absolute.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        }

        if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains(':'))
        {
            return false;
        }

        var segments = value.Replace('\\', '/').Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not "." and not "..");
    }

    private static bool UpdateHex(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
