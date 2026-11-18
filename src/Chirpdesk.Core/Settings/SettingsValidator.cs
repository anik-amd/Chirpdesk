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

        // Existing installs predate auto-warmup: pin their services so the hidden-warm
        // behaviour is on by default without forcing the user to reopen Settings.
        if (settings.SchemaVersion < 4)
        {
            foreach (var instance in settings.Services)
            {
                instance.WarmAtBoot = true;
            }
        }

        settings.Theme = Theming.ThemeDecision.ToStorageValue(Theming.ThemeDecision.Parse(settings.Theme));

        settings.RailMode = settings.RailMode?.Trim().ToLowerInvariant() switch
        {
            "hidden" => "hidden",
            "hover" => "hover",
            _ => "always",
        };

        settings.LightTheme = string.IsNullOrWhiteSpace(settings.LightTheme)
            ? "latte"
            : settings.LightTheme.Trim().ToLowerInvariant();
        settings.DarkTheme = string.IsNullOrWhiteSpace(settings.DarkTheme)
            ? "mocha"
            : settings.DarkTheme.Trim().ToLowerInvariant();

        settings.CacheBudgetMb = settings.CacheBudgetMb <= 0
            ? AppSettings.DefaultCacheBudgetMb
            : Math.Clamp(settings.CacheBudgetMb, AppSettings.MinCacheBudgetMb, AppSettings.MaxCacheBudgetMb);

        settings.GlobalCacheBudgetMb = settings.GlobalCacheBudgetMb <= 0
            ? AppSettings.DefaultGlobalCacheBudgetMb
            : Math.Clamp(settings.GlobalCacheBudgetMb, AppSettings.MinCacheBudgetMb, AppSettings.MaxGlobalCacheBudgetMb);

        if (settings.GlobalCacheBudgetMb < settings.CacheBudgetMb)
        {
            settings.GlobalCacheBudgetMb = settings.CacheBudgetMb;
        }

        if (settings.ReclaimedBytes < 0)
        {
            settings.ReclaimedBytes = 0;
        }

        // A bad plugin id is ignored, never fatal: the plugin simply stays active.
        settings.DisabledPlugins = (settings.DisabledPlugins ?? [])
            .Select(id => id?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(IsValidId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var instance in settings.Services)
        {
            if (string.IsNullOrWhiteSpace(instance.ProfileId))
            {
                instance.ProfileId = instance.InstanceId;
            }

            instance.Blocking = string.IsNullOrWhiteSpace(instance.Blocking) ? "default" : instance.Blocking;

            instance.PageDarkMode = instance.PageDarkMode?.Trim().ToLowerInvariant() switch
            {
                null or "" => null,
                "auto" => "auto",
                "on" => "on",
                "off" => "off",
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
