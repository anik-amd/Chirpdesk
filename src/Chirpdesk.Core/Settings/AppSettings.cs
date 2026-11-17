namespace Chirpdesk.Core.Settings;

/// <summary>The whole user configuration. Small enough to rewrite atomically on every change.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>. One palette; no colour families yet.</summary>
    public string Theme { get; set; } = "system";

    public List<ServiceInstance> Services { get; set; } = [];
}
