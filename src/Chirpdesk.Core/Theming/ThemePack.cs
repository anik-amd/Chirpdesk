namespace Chirpdesk.Core.Theming;

/// <summary>One selectable palette inside a theme plugin, e.g. Catppuccin Mocha.</summary>
public sealed record ThemeFlavor(
    string Id,
    string Name,
    string Mode,
    string Swatch);

/// <summary>A bundled theme plugin: a set of light and dark flavours.</summary>
public sealed class ThemePack
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public List<ThemeFlavor> Flavors { get; set; } = [];
}
