namespace Chirpdesk.Core.Plugins;

/// <summary>
/// The manifest every plugin folder carries. Data only (ADR 0007): it names the plugin, its
/// kind and the API it targets; it cannot point outside its own folder.
/// </summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int ApiVersion { get; set; }

    public string? Author { get; set; }

    public string? Homepage { get; set; }

    public string? Description { get; set; }

    /// <summary>Brand accent for the service badge.</summary>
    public string? Accent { get; set; }

    public List<string>? Permissions { get; set; } = [];

    public List<string>? Assets { get; set; } = [];
}
