using System.Text.Json;
using System.Text.RegularExpressions;

namespace Chirpdesk.Core.Integrations;

/// <summary>One command-bar entry an integration helper exposes.</summary>
public sealed class IntegrationCommand
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>`none` or `text`; text means the bar asks for one line before dispatch.</summary>
    public string Argument { get; set; } = "none";
}

/// <summary>
/// `integration.json`: the technical half of an integration plugin. `plugin.json` still carries
/// the id, version and permissions; this file names the executable and the commands it exposes.
/// </summary>
public sealed class IntegrationManifest
{
    public int ProtocolVersion { get; set; }

    public string Executable { get; set; } = string.Empty;

    public List<IntegrationCommand> Commands { get; set; } = [];
}

/// <summary>
/// Validates `integration.json` before any helper is installed or launched. The host supports
/// exactly one protocol version at a time, so a mismatch is a hard refusal rather than a warning.
/// </summary>
public static partial class IntegrationManifestCodec
{
    public const int CurrentProtocolVersion = 1;
    public const int MaxCommands = 20;
    public const int MaxExecutableNameLength = 80;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> ArgumentModes = new(StringComparer.Ordinal)
    {
        "none", "text",
    };

    public static IntegrationManifest? TryRead(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "integration.json is empty";
            return null;
        }

        IntegrationManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<IntegrationManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            error = $"integration.json is not valid json: {ex.Message}";
            return null;
        }

        if (manifest is null)
        {
            error = "integration.json is null";
            return null;
        }

        var problems = Validate(manifest);

        if (problems.Count > 0)
        {
            error = string.Join("; ", problems);
            return null;
        }

        return manifest;
    }

    public static IReadOnlyList<string> Validate(IntegrationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var problems = new List<string>();

        if (manifest.ProtocolVersion != CurrentProtocolVersion)
        {
            problems.Add(
                $"protocolVersion {manifest.ProtocolVersion} is not supported; the host speaks {CurrentProtocolVersion}");
        }

        if (!IsPlainExecutableName(manifest.Executable))
        {
            problems.Add($"executable '{manifest.Executable}' must be a plain .exe file name");
        }

        manifest.Commands ??= [];

        if (manifest.Commands.Count > MaxCommands)
        {
            problems.Add($"at most {MaxCommands} commands are allowed");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var command in manifest.Commands)
        {
            if (command is null)
            {
                problems.Add("a command entry is null");
                continue;
            }

            if (!CommandIdPattern().IsMatch(command.Id ?? string.Empty))
            {
                problems.Add($"invalid command id '{command.Id}'");
            }
            else if (!ids.Add(command.Id ?? string.Empty))
            {
                problems.Add($"duplicate command id '{command.Id}'");
            }

            if (string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 40)
            {
                problems.Add($"command '{command.Id}' has no title or a title longer than 40 characters");
            }

            if (command.Description is { Length: > 120 })
            {
                problems.Add($"command '{command.Id}' has a description longer than 120 characters");
            }

            if (!ArgumentModes.Contains(command.Argument ?? string.Empty))
            {
                problems.Add($"command '{command.Id}' has an unknown argument mode '{command.Argument}'");
            }
        }

        return problems;
    }

    /// <summary>
    /// A helper is resolved inside its plugin folder by file name only. No separators, no parent
    /// segments, no drive or stream syntax; the caller still resolves and checks the file exists.
    /// </summary>
    public static bool IsPlainExecutableName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxExecutableNameLength)
        {
            return false;
        }

        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !value.Contains('/')
            && !value.Contains('\\')
            && !value.Contains("..", StringComparison.Ordinal)
            && !value.Contains(':')
            && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandIdPattern();
}
