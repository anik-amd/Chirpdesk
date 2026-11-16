namespace Chirpdesk.Core.Settings;

/// <summary>The whole user configuration. Small enough to rewrite atomically on every change.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public List<ServiceInstance> Services { get; set; } = [];
}
