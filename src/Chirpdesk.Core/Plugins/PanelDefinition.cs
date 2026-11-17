namespace Chirpdesk.Core.Plugins;

/// <summary>A bundled tool panel. The host renders the closed primitive set; the plugin only picks.</summary>
public sealed class PanelDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Subtitle { get; set; }

    public List<PanelSection> Sections { get; set; } = [];

    public List<PanelAction> Actions { get; set; } = [];
}

public sealed class PanelSection
{
    /// <summary><c>list</c>, <c>table</c>, <c>text</c>, <c>progress</c>, <c>button</c>, <c>separator</c>.</summary>
    public string Type { get; set; } = string.Empty;

    public string? Id { get; set; }

    public string? Text { get; set; }

    public string? EmptyText { get; set; }

    public List<PanelColumn>? Columns { get; set; }

    public PanelSource? Source { get; set; }
}

public sealed class PanelColumn
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Width { get; set; } = "auto";
}

public sealed class PanelSource
{
    public string Action { get; set; } = string.Empty;
}

public sealed class PanelAction
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary><c>storageReport</c>, <c>trimCache</c>, <c>scanOrphans</c>, <c>deleteOrphan</c>, <c>revealFolder</c>, <c>openSettings</c>.</summary>
    public string Primitive { get; set; } = string.Empty;

    public string Style { get; set; } = "secondary";

    public PanelActionArgs? Args { get; set; }

    public string? Confirm { get; set; }
}

public sealed class PanelActionArgs
{
    public int? BudgetMb { get; set; }

    public string? Targets { get; set; }

    public string? Root { get; set; }

    public string? Section { get; set; }
}
