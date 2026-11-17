namespace Chirpdesk.Core.Plugins;

/// <summary>
/// Fail open per element: an unknown primitive disables that action, not the panel; an unknown
/// section is removed and reported. The host still renders what it understands.
/// </summary>
public static class PanelValidator
{
    private static readonly HashSet<string> SectionTypes = new(StringComparer.Ordinal)
    {
        "list", "table", "text", "progress", "button", "separator",
    };

    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "storageReport", "trimCache", "scanOrphans", "deleteOrphan", "revealFolder", "openSettings",
    };

    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal)
    {
        "profiles", "plugins", "logs", "data",
    };

    public static IReadOnlyList<string> Validate(PanelDefinition panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(panel.Title))
        {
            errors.Add("panel has no title");
        }

        panel.Sections.RemoveAll(section =>
        {
            if (!SectionTypes.Contains(section.Type ?? string.Empty))
            {
                errors.Add($"unknown panel section '{section.Type}'");
                return true;
            }

            if (section.Type == "text" && string.IsNullOrWhiteSpace(section.Text))
            {
                errors.Add("text section has no value");
                return true;
            }

            return false;
        });

        panel.Actions.RemoveAll(action =>
        {
            if (string.IsNullOrWhiteSpace(action.Id) || string.IsNullOrWhiteSpace(action.Label))
            {
                errors.Add("panel action is missing an id or label");
                return true;
            }

            if (!Actions.Contains(action.Primitive ?? string.Empty))
            {
                errors.Add($"unknown panel action '{action.Primitive}'");
                return true;
            }

            var args = action.Args;

            if (action.Primitive == "trimCache")
            {
                if (args?.BudgetMb is < 1 or > 2000)
                {
                    errors.Add($"trimCache action '{action.Id}' has an invalid budget");
                    return true;
                }

                if (args?.Targets is { Length: > 0 } targets
                    && targets is not ("selected" or "all")
                    && targets.Length > 32)
                {
                    errors.Add($"trimCache action '{action.Id}' has an invalid target");
                    return true;
                }
            }

            if (action.Primitive == "revealFolder" && !Roots.Contains(args?.Root ?? string.Empty))
            {
                errors.Add($"revealFolder action '{action.Id}' must name a known root");
                return true;
            }

            return false;
        });

        return errors;
    }
}
