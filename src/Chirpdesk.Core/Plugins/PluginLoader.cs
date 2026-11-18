using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chirpdesk.Core.Blocking;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Theming;

namespace Chirpdesk.Core.Plugins;

public sealed record PluginScanRoot(string Directory, bool Bundled);

/// <summary>An installed plugin of any kind. Only <c>service</c> plugins produce palette rows.</summary>
public sealed record InstalledPlugin(
    string Id,
    string Kind,
    string Name,
    string? Description,
    string? Accent,
    bool Bundled,
    string? Version = null,
    PluginSource? Source = null);

/// <summary>What one scan produced. Errors are per plugin, never fatal (invariant 8).</summary>
public sealed record PluginLoadResult(
    IReadOnlyList<ServiceDefinition> Definitions,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Shadowed);

/// <summary>
/// Finds `service` plugins on disk and turns them into definitions. A folder needs
/// <c>plugin.json</c> and, for a service, <c>service.json</c>. One bad folder is skipped and
/// reported; it never prevents the app from starting and never takes other plugins with it.
/// </summary>
public static partial class PluginLoader
{
    public const string ServiceKind = "service";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        ServiceKind, "theme", "filter", "tool", "integration",
    };

    private static readonly HashSet<string> KnownPermissions = new(StringComparer.Ordinal)
    {
        "css", "notifications", "filters", "tools", "read-storage", "trim-cache", "storage",
        "inject-js", "process",
    };

    private static readonly HashSet<string> UnavailablePermissions = new(StringComparer.Ordinal)
    {
        "inject-js", "process",
    };

    private static readonly HashSet<string> BundledOnlyPermissions = new(StringComparer.Ordinal)
    {
        "tools", "read-storage", "trim-cache", "storage",
    };

    /// <summary>
    /// Validates a plugin that has just been extracted from a repository. The caller compares
    /// the returned manifest id/kind/version with the signed index before moving the folder into
    /// the live plugin root.
    /// </summary>
    public static PluginManifest? ReadInstalledManifest(
        string folder,
        out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        var manifest = ReadManifest(folder, NameOf(folder), bundled: false, problems);
        errors = problems;
        return manifest;
    }

    public static PluginLoadResult LoadServiceDefinitions(IEnumerable<PluginScanRoot> roots, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var loaded = new Dictionary<string, LoadedPlugin>(StringComparer.Ordinal);
        var errors = new List<string>();
        var shadowed = new List<string>();

        foreach (var root in roots)
        {
            foreach (var folder in EnumeratePluginFolders(root.Directory, errors))
            {
                var folderName = NameOf(folder);
                var manifest = ReadManifest(folder, folderName, root.Bundled, errors);

                if (manifest is null || !string.Equals(manifest.Kind, ServiceKind, StringComparison.Ordinal))
                {
                    // Other kinds are valid plugins for a loader this task does not build yet.
                    continue;
                }

                var definition = ReadDefinition(folder, folderName, manifest, errors);
                if (definition is null)
                {
                    continue;
                }

                if (loaded.TryGetValue(definition.Id, out var existing))
                {
                    if (existing.Bundled != root.Bundled)
                    {
                        // A user plugin always wins over a bundled plugin of the same id,
                        // whichever order the roots were scanned in.
                        if (!root.Bundled)
                        {
                            loaded[definition.Id] = new LoadedPlugin(definition, root.Bundled);
                        }

                        if (!shadowed.Contains(definition.Id, StringComparer.Ordinal))
                        {
                            shadowed.Add(definition.Id);
                        }
                    }
                    else
                    {
                        errors.Add($"{folderName}: duplicate id '{definition.Id}'");
                    }

                    continue;
                }

                loaded[definition.Id] = new LoadedPlugin(definition, root.Bundled);
            }
        }

        foreach (var error in errors)
        {
            log?.Warn($"plugin skipped: {error}");
        }

        foreach (var id in shadowed)
        {
            log?.Info($"plugin '{id}' shadows the bundled definition of the same id");
        }

        var definitions = loaded.Values
            .Select(entry => entry.Definition)
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .ToList();

        return new PluginLoadResult(definitions, errors, shadowed);
    }

    /// <summary>Lists every valid installed plugin, regardless of kind, for the plugin browser.</summary>
    public static IReadOnlyList<InstalledPlugin> LoadInstalledPlugins(
        IEnumerable<PluginScanRoot> roots,
        ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var installed = new Dictionary<string, InstalledPlugin>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var root in roots)
        {
            foreach (var folder in EnumeratePluginFolders(root.Directory, errors))
            {
                var folderName = NameOf(folder);
                var manifest = ReadManifest(folder, folderName, root.Bundled, errors);
                if (manifest is null)
                {
                    continue;
                }

                var plugin = new InstalledPlugin(
                    manifest.Id,
                    manifest.Kind,
                    manifest.Name,
                    manifest.Description,
                    manifest.Accent,
                    root.Bundled,
                    manifest.Version,
                    root.Bundled ? null : PluginSourceStore.TryRead(folder));

                if (installed.TryGetValue(manifest.Id, out var existing) && existing.Bundled && !root.Bundled)
                {
                    installed[manifest.Id] = plugin;
                }
                else if (!installed.ContainsKey(manifest.Id))
                {
                    installed[manifest.Id] = plugin;
                }
            }
        }

        foreach (var error in errors)
        {
            log?.Warn($"plugin inventory skipped: {error}");
        }

        return installed.Values
            .OrderBy(plugin => plugin.Kind, StringComparer.Ordinal)
            .ThenBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Loads bundled `tool` panels. User-installed tools are refused in v1 because a tool may
    /// only surface host capability; the same validator still runs on the bundled panel.
    /// </summary>
    public static IReadOnlyList<PanelDefinition> LoadPanels(IEnumerable<PluginScanRoot> roots, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var panels = new List<PanelDefinition>();
        var errors = new List<string>();

        foreach (var root in roots)
        {
            foreach (var folder in EnumeratePluginFolders(root.Directory, errors))
            {
                var folderName = NameOf(folder);
                var manifest = ReadManifest(folder, folderName, root.Bundled, errors);

                if (manifest is null || !string.Equals(manifest.Kind, "tool", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!root.Bundled)
                {
                    errors.Add($"{folderName}: user-installed tools are bundled-only in v1");
                    continue;
                }

                var panel = ReadJson<PanelDefinition>(folder, folderName, "panel.json", errors);
                if (panel is null)
                {
                    continue;
                }

                panel.Id = manifest.Id;
                errors.AddRange(PanelValidator.Validate(panel).Select(problem => $"{folderName}: {problem}"));

                if (!string.IsNullOrWhiteSpace(panel.Title))
                {
                    panels.Add(panel);
                }
            }
        }

        foreach (var error in errors)
        {
            log?.Warn($"panel skipped: {error}");
        }

        return panels;
    }

    /// <summary>
    /// Loads `filter` plugins. User packs shadow bundled packs of the same id; a broken pack is
    /// skipped and logged, never fatal (invariant 8).
    /// </summary>
    public static IReadOnlyList<FilterPack> LoadFilters(IEnumerable<PluginScanRoot> roots, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var loaded = new Dictionary<string, (FilterPack Pack, bool Bundled)>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var root in roots)
        {
            foreach (var folder in EnumeratePluginFolders(root.Directory, errors))
            {
                var folderName = NameOf(folder);
                var manifest = ReadManifest(folder, folderName, root.Bundled, errors);

                if (manifest is null || !string.Equals(manifest.Kind, "filter", StringComparison.Ordinal))
                {
                    continue;
                }

                var pack = ReadJson<FilterPack>(folder, folderName, "filters.json", errors);
                if (pack is null)
                {
                    continue;
                }

                pack.Id = manifest.Id;

                if (loaded.TryGetValue(pack.Id, out var existing))
                {
                    if (existing.Bundled && !root.Bundled)
                    {
                        loaded[pack.Id] = (pack, root.Bundled);
                    }
                    else if (existing.Bundled == root.Bundled)
                    {
                        errors.Add($"{folderName}: duplicate filter id '{pack.Id}'");
                    }

                    continue;
                }

                loaded[pack.Id] = (pack, root.Bundled);
            }
        }

        foreach (var error in errors)
        {
            log?.Warn($"filter pack skipped: {error}");
        }

        return loaded.Values
            .Select(entry => entry.Pack)
            .OrderBy(pack => pack.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Loads `theme` plugins. A flavour must have a safe id, a mode and a hex swatch; bad
    /// flavours are dropped with a log line rather than taking the whole pack down.
    /// </summary>
    public static IReadOnlyList<ThemePack> LoadThemes(IEnumerable<PluginScanRoot> roots, ILog? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var loaded = new Dictionary<string, (ThemePack Pack, bool Bundled)>(StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var root in roots)
        {
            foreach (var folder in EnumeratePluginFolders(root.Directory, errors))
            {
                var folderName = NameOf(folder);
                var manifest = ReadManifest(folder, folderName, root.Bundled, errors);

                if (manifest is null || !string.Equals(manifest.Kind, "theme", StringComparison.Ordinal))
                {
                    continue;
                }

                // A theme plugin may be a host capability (page-dark) rather than a palette pack.
                if (!File.Exists(Path.Combine(folder, "themes.json")))
                {
                    continue;
                }

                var pack = ReadJson<ThemePack>(folder, folderName, "themes.json", errors);
                if (pack is null)
                {
                    continue;
                }

                pack.Id = manifest.Id;
                pack.Flavors = pack.Flavors
                    .Where(flavor => IsValidFlavor(flavor, folderName, errors))
                    .GroupBy(flavor => flavor.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();

                if (pack.Flavors.Count == 0)
                {
                    errors.Add($"{folderName}: no valid flavours");
                    continue;
                }

                if (loaded.TryGetValue(pack.Id, out var existing))
                {
                    if (existing.Bundled && !root.Bundled)
                    {
                        loaded[pack.Id] = (pack, root.Bundled);
                    }
                    else if (existing.Bundled == root.Bundled)
                    {
                        errors.Add($"{folderName}: duplicate theme id '{pack.Id}'");
                    }

                    continue;
                }

                loaded[pack.Id] = (pack, root.Bundled);
            }
        }

        foreach (var error in errors)
        {
            log?.Warn($"theme pack skipped: {error}");
        }

        return loaded.Values
            .Select(entry => entry.Pack)
            .OrderBy(pack => pack.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsValidFlavor(ThemeFlavor flavor, string folderName, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(flavor.Id)
            || !Regex.IsMatch(flavor.Id, "^[a-z0-9-]{1,24}$", RegexOptions.CultureInvariant))
        {
            errors.Add($"{folderName}: invalid flavour id '{flavor.Id}'");
            return false;
        }

        if (string.IsNullOrWhiteSpace(flavor.Name))
        {
            errors.Add($"{folderName}: flavour '{flavor.Id}' has no name");
            return false;
        }

        if (flavor.Mode is not ("light" or "dark"))
        {
            errors.Add($"{folderName}: flavour '{flavor.Id}' has mode '{flavor.Mode}'");
            return false;
        }

        if (string.IsNullOrWhiteSpace(flavor.Swatch) || !Regex.IsMatch(flavor.Swatch, "^#[0-9a-fA-F]{6}$"))
        {
            errors.Add($"{folderName}: flavour '{flavor.Id}' has invalid swatch");
            return false;
        }

        return true;
    }

    private static List<string> EnumeratePluginFolders(string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.GetDirectories(directory).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{NameOf(directory)}: cannot list plugins: {ex.Message}");
            return [];
        }
    }

    private static PluginManifest? ReadManifest(string folder, string folderName, bool bundled, List<string> errors)
    {
        var manifest = ReadJson<PluginManifest>(folder, folderName, "plugin.json", errors);
        if (manifest is null)
        {
            return null;
        }

        var problems = ValidateManifest(manifest, bundled);
        if (problems.Count > 0)
        {
            errors.Add($"{folderName}: {string.Join("; ", problems)}");
            return null;
        }

        return manifest;
    }

    private static ServiceDefinition? ReadDefinition(
        string folder,
        string folderName,
        PluginManifest manifest,
        List<string> errors)
    {
        var json = ReadJsonText(folder, folderName, "service.json", errors);
        if (json is null)
        {
            return null;
        }

        var definition = DefinitionLoader.Deserialize(json, out var parseError);
        if (definition is null)
        {
            errors.Add($"{folderName}: invalid service.json: {parseError}");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(definition.Id)
            && !string.Equals(definition.Id.Trim(), manifest.Id, StringComparison.Ordinal))
        {
            errors.Add(
                $"{folderName}: definition id '{definition.Id.Trim()}' does not match plugin id '{manifest.Id}'");
            return null;
        }

        definition.Id = manifest.Id;

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            definition.Name = manifest.Name;
        }

        if (string.IsNullOrWhiteSpace(definition.Description))
        {
            definition.Description = manifest.Description;
        }

        if (string.IsNullOrWhiteSpace(definition.Accent))
        {
            definition.Accent = manifest.Accent;
        }

        DefinitionValidator.Normalize(definition);

        var problems = DefinitionValidator.Validate(definition);
        if (problems.Count > 0)
        {
            errors.Add($"{folderName}: {string.Join("; ", problems)}");
            return null;
        }

        return definition;
    }

    private static List<string> ValidateManifest(PluginManifest manifest, bool bundled)
    {
        var problems = new List<string>();

        if (!IdPattern().IsMatch(manifest.Id ?? string.Empty))
        {
            problems.Add($"invalid id '{manifest.Id}'");
        }

        if (!Kinds.Contains(manifest.Kind ?? string.Empty))
        {
            problems.Add($"unknown kind '{manifest.Kind}'");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            problems.Add("has no name");
        }

        if (!VersionPattern().IsMatch(manifest.Version ?? string.Empty))
        {
            problems.Add($"invalid version '{manifest.Version}' (expected semver, e.g. 1.0.0)");
        }

        if (manifest.ApiVersion != DefinitionValidator.HostApiVersion)
        {
            problems.Add(
                $"targets apiVersion {manifest.ApiVersion}; this host supports {DefinitionValidator.HostApiVersion}");
        }

        foreach (var permission in manifest.Permissions ?? [])
        {
            if (!KnownPermissions.Contains(permission))
            {
                problems.Add($"unknown permission '{permission}'");
                continue;
            }

            if (UnavailablePermissions.Contains(permission))
            {
                problems.Add($"permission '{permission}' is not available in v1");
                continue;
            }

            if (!bundled && BundledOnlyPermissions.Contains(permission))
            {
                problems.Add($"permission '{permission}' is bundled-only in v1");
            }
        }

        foreach (var asset in manifest.Assets ?? [])
        {
            if (!IsPlainFileName(asset))
            {
                problems.Add($"asset '{asset}' must be a file name, not a path");
            }
        }

        return problems;
    }

    private static string? ReadJsonText(string folder, string folderName, string fileName, List<string> errors)
    {
        var path = Path.Combine(folder, fileName);

        if (!File.Exists(path))
        {
            errors.Add($"{folderName}: missing {fileName}");
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{folderName}: cannot read {fileName}: {ex.Message}");
            return null;
        }
    }

    private static T? ReadJson<T>(string folder, string folderName, string fileName, List<string> errors)
        where T : class
    {
        var json = ReadJsonText(folder, folderName, fileName, errors);
        if (json is null)
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(json, Options);
            if (value is null)
            {
                errors.Add($"{folderName}: empty {fileName}");
            }

            return value;
        }
        catch (JsonException ex)
        {
            errors.Add($"{folderName}: invalid {fileName}: {ex.Message}");
            return null;
        }
    }

    private static bool IsPlainFileName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('/', StringComparison.Ordinal)
        && !value.Contains('\\', StringComparison.Ordinal)
        && !value.Contains("..", StringComparison.Ordinal)
        && value.IndexOf(':') < 0;

    private static string NameOf(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? directory : name;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    private readonly record struct LoadedPlugin(ServiceDefinition Definition, bool Bundled);
}
