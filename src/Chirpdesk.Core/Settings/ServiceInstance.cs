namespace Chirpdesk.Core.Settings;

/// <summary>
/// One site the user actually runs: one profile, one unread count, one warm/cold state.
/// A definition (plugin) is shared; an instance is this row. <see cref="DefinitionId"/> being
/// null means a custom site the user added by URL — the built-in flow, never a plugin.
/// </summary>
public sealed class ServiceInstance
{
    public string InstanceId { get; set; } = string.Empty;

    public string? DefinitionId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Absolute <c>https</c> URL. Null means "use the definition's URL".</summary>
    public string? Url { get; set; }

    public string? IconUri { get; set; }

    public string? Accent { get; set; }

    public bool WarmAtBoot { get; set; }

    public string? UnreadRegex { get; set; }

    public string? UserAgent { get; set; }

    public string? InjectCss { get; set; }

    public bool Mute { get; set; }

    /// <summary><c>default</c>, <c>off</c> or <c>+list</c> (opt-in list import, phase 7).</summary>
    public string Blocking { get; set; } = "default";

    /// <summary><c>native</c>, <c>shim</c> or <c>title-only</c>; null means "ask the definition, then probe".</summary>
    public string? NotificationMode { get; set; }

    /// <summary>
    /// <c>off</c> (default) or <c>auto</c>. Auto applies the invert filter only when the page
    /// still measures light after <c>prefers-color-scheme</c>, i.e. only to sites without a
    /// built-in dark theme. A definition sets the default for its site, because the site —
    /// not the user — knows whether it has one; the instance can override.
    /// </summary>
    public string PageDarkMode { get; set; } = "off";

    /// <summary>Instance id of the profile owner when this instance shares one; null when standalone.</summary>
    public string? ShareProfileWith { get; set; }

    /// <summary>Defaults to <see cref="InstanceId"/>. Enforced by the validator.</summary>
    public string? ProfileId { get; set; }

    public int Order { get; set; }
}
