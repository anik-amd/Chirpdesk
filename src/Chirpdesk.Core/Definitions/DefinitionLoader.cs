using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chirpdesk.Core.Definitions;

/// <summary>
/// Reads definitions from JSON. Fail open at the file level: one bad definition is skipped
/// and reported, never fatal, and never replaced by a guess.
/// </summary>
public static class DefinitionLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ServiceDefinition? Parse(string json, out string? error)
    {
        var definition = Deserialize(json, out error);
        if (definition is null)
        {
            return null;
        }

        DefinitionValidator.Normalize(definition);
        var errors = DefinitionValidator.Validate(definition);
        if (errors.Count > 0)
        {
            error = string.Join("; ", errors);
            return null;
        }

        return definition;
    }

    /// <summary>
    /// Reads the JSON without normalising or validating it, so a plugin can fill `id` and
    /// `name` from its manifest first. Never throws.
    /// </summary>
    internal static ServiceDefinition? Deserialize(string json, out string? error)
    {
        error = null;

        ServiceDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<ServiceDefinition>(json, Options);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }

        if (definition is null)
        {
            error = "empty definition";
        }

        return definition;
    }

    /// <summary>Loads many definitions, skipping invalid ones and collecting one error per skip.</summary>
    public static IReadOnlyList<ServiceDefinition> Load(IEnumerable<string> documents, ICollection<string> errors)
    {
        var definitions = new List<ServiceDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var json in documents)
        {
            var definition = Parse(json, out var error);
            if (definition is null)
            {
                errors.Add(error ?? "invalid definition");
                continue;
            }

            if (!seen.Add(definition.Id))
            {
                errors.Add($"duplicate definition id '{definition.Id}'");
                continue;
            }

            definitions.Add(definition);
        }

        return definitions;
    }
}
