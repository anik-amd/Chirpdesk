using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Settings;

/// <summary>
/// The schema rules that matter for safety: ids are path components, so they are the only thing
/// between a settings file and a directory traversal. Everything else is cosmetic and is
/// normalised rather than rejected.
/// </summary>
internal static partial class SettingsValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static void Normalize(AppSettings settings)
    {
        settings.SchemaVersion = settings.SchemaVersion <= 0
            ? AppSettings.CurrentSchemaVersion
            : settings.SchemaVersion;

        settings.Theme = Theming.ThemeDecision.ToStorageValue(Theming.ThemeDecision.Parse(settings.Theme));

        foreach (var instance in settings.Services)
        {
            if (string.IsNullOrWhiteSpace(instance.ProfileId))
            {
                instance.ProfileId = instance.InstanceId;
            }

            instance.Blocking = string.IsNullOrWhiteSpace(instance.Blocking) ? "default" : instance.Blocking;

            instance.PageDarkMode = instance.PageDarkMode?.Trim().ToLowerInvariant() switch
            {
                "auto" => "auto",
                _ => "off",
            };
        }
    }

    public static IReadOnlyList<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();

        if (settings.SchemaVersion is < 1 or > AppSettings.CurrentSchemaVersion)
        {
            errors.Add($"unsupported schemaVersion {settings.SchemaVersion}");
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var instance in settings.Services)
        {
            var id = instance.InstanceId;
            if (!IsValidId(id))
            {
                errors.Add($"invalid instanceId '{id}'");
                continue;
            }

            if (!seenIds.Add(id))
            {
                errors.Add($"duplicate instanceId '{id}'");
            }

            if (!IsValidId(instance.ProfileId))
            {
                errors.Add($"invalid profileId '{instance.ProfileId}' for '{id}'");
            }

            if (string.IsNullOrWhiteSpace(instance.Name))
            {
                errors.Add($"missing name for '{id}'");
            }

            if (instance.DefinitionId is null && string.IsNullOrWhiteSpace(instance.Url))
            {
                errors.Add($"'{id}' has no definition and no url");
            }

            if (!string.IsNullOrWhiteSpace(instance.Url) && !IsHttpsUrl(instance.Url))
            {
                errors.Add($"'{id}' has a non-https url");
            }
        }

        return errors;
    }

    private static bool IsValidId(string? value) =>
        !string.IsNullOrEmpty(value) && IdPattern().IsMatch(value);

    private static bool IsHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo);
}
