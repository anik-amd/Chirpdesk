using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Definitions;

/// <summary>
/// Validates a definition before it is allowed to become a runtime. Fail open in the product
/// sense (an invalid definition is skipped and logged, never fatal) but strict here: a
/// definition that names a bad URL, a traversal path or a broken regex is rejected rather
/// than guessed at.
/// </summary>
public static partial class DefinitionValidator
{
    public const int HostApiVersion = 1;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly string[] NotificationModes =
        [ServiceDefinition.NativeMode, ServiceDefinition.ShimMode, ServiceDefinition.TitleOnlyMode];
    private static readonly string[] PageDarkModes =
        [ServiceDefinition.PageDarkOff, ServiceDefinition.PageDarkAuto, ServiceDefinition.PageDarkOn];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Applies defaults and lower-cases the enum-like fields. Never throws.</summary>
    public static void Normalize(ServiceDefinition definition)
    {
        definition.Id = definition.Id?.Trim().ToLowerInvariant() ?? string.Empty;
        definition.Name = definition.Name?.Trim() ?? string.Empty;
        definition.Url = definition.Url?.Trim() ?? string.Empty;
        definition.NotificationMode = NormalizeChoice(definition.NotificationMode, NotificationModes, ServiceDefinition.NativeMode);
        definition.PageDarkMode = NormalizeChoice(definition.PageDarkMode, PageDarkModes, ServiceDefinition.PageDarkOff);

        if (definition.Aliases is not null)
        {
            definition.Aliases = definition.Aliases
                .Select(alias => alias?.Trim() ?? string.Empty)
                .Where(alias => alias.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (definition.MinApiVersion <= 0)
        {
            definition.MinApiVersion = 1;
        }

        if (definition.ApiVersion <= 0)
        {
            definition.ApiVersion = HostApiVersion;
        }
    }

    public static IReadOnlyList<string> Validate(ServiceDefinition definition)
    {
        var errors = new List<string>();

        if (!IdPattern().IsMatch(definition.Id))
        {
            errors.Add($"invalid id '{definition.Id}'");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            errors.Add($"'{definition.Id}' has no name");
        }

        if (!IsHttpsUrl(definition.Url))
        {
            errors.Add($"'{definition.Id}' has an invalid url");
        }

        if (definition.Icon is { Length: > 0 } icon
            && (icon.Contains('/', StringComparison.Ordinal)
                || icon.Contains('\\', StringComparison.Ordinal)
                || icon.Contains("..", StringComparison.Ordinal)))
        {
            errors.Add($"'{definition.Id}' icon must be a file name, not a path");
        }

        if (!NotificationModes.Contains(definition.NotificationMode, StringComparer.Ordinal))
        {
            errors.Add($"'{definition.Id}' has an unknown notification mode '{definition.NotificationMode}'");
        }

        if (!PageDarkModes.Contains(definition.PageDarkMode, StringComparer.Ordinal))
        {
            errors.Add($"'{definition.Id}' has an unknown page dark mode '{definition.PageDarkMode}'");
        }

        if (definition.UnreadRegex is { Length: > 0 } pattern && !IsValidRegex(pattern))
        {
            errors.Add($"'{definition.Id}' has an invalid unread regex");
        }

        if (definition.Aliases is { } aliases)
        {
            if (aliases.Count > 16)
            {
                errors.Add($"'{definition.Id}' has more than 16 aliases");
            }

            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || alias.Length > 32)
                {
                    errors.Add($"'{definition.Id}' has an invalid alias");
                    break;
                }
            }
        }

        if (definition.ApiVersion != HostApiVersion)
        {
            errors.Add($"'{definition.Id}' targets apiVersion {definition.ApiVersion}, host is {HostApiVersion}");
        }

        if (definition.MinApiVersion < 1 || definition.MinApiVersion > definition.ApiVersion)
        {
            errors.Add($"'{definition.Id}' has minApiVersion {definition.MinApiVersion} above apiVersion {definition.ApiVersion}");
        }

        if (definition.Activation is { } activation)
        {
            errors.AddRange(ValidateActivation(definition.Id, activation));
        }

        return errors;
    }

    private static IEnumerable<string> ValidateActivation(string id, NotificationActivation activation)
    {
        var template = activation.UrlTemplate ?? string.Empty;

        if (!template.Contains("{tag}", StringComparison.Ordinal))
        {
            yield return $"'{id}' activation template has no {{tag}} placeholder";
        }

        if (!IsHttpsUrl(template.Replace("{tag}", "x", StringComparison.Ordinal)))
        {
            yield return $"'{id}' activation template is not an absolute https url";
        }

        if (activation.TagPattern is { Length: > 0 } tagPattern && !IsValidRegex(tagPattern))
        {
            yield return $"'{id}' activation tagPattern is not a valid regex";
        }
    }

    private static string NormalizeChoice(string? value, IReadOnlyList<string> allowed, string fallback)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(trimmed) && allowed.Contains(trimmed, StringComparer.Ordinal)
            ? trimmed
            : fallback;
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsHttpsUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo);
}
