namespace Chirpdesk.Core.Definitions;

/// <summary>
/// A shipped service definition. Data only (ADR 0007): it can describe where a site lives,
/// how it reports unread counts, which notification path it uses and whether it has its own
/// dark theme. It cannot execute code, name a filesystem path, or add a host capability.
/// </summary>
public sealed class ServiceDefinition
{
    public const string NativeMode = "native";
    public const string ShimMode = "shim";
    public const string TitleOnlyMode = "title-only";

    public const string PageDarkOff = "off";
    public const string PageDarkAuto = "auto";

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    /// <summary>File name inside the plugin archive. Never a path.</summary>
    public string? Icon { get; set; }

    /// <summary>Title pattern with one capture group; null means the default <c>\((\d+)\)</c>.</summary>
    public string? UnreadRegex { get; set; }

    /// <summary><c>native</c>, <c>shim</c> or <c>title-only</c>.</summary>
    public string NotificationMode { get; set; } = NativeMode;

    /// <summary>
    /// <c>off</c> when the site has its own theme, <c>auto</c> to let the host darken the page
    /// when it still measures light (ADR 0010). The definition knows; the user may override.
    /// </summary>
    public string PageDarkMode { get; set; } = PageDarkOff;

    public string? UserAgent { get; set; }

    /// <summary>Short search aliases, e.g. <c>wa</c> for WhatsApp. Optional.</summary>
    public List<string>? Aliases { get; set; }

    /// <summary>How a notification click becomes a route. Optional: without it a click opens the site.</summary>
    public NotificationActivation? Activation { get; set; }

    /// <summary>Oldest host API this definition still works against.</summary>
    public int MinApiVersion { get; set; } = 1;

    public int ApiVersion { get; set; } = 1;
}

/// <summary>
/// A route template over the notification's tag, e.g.
/// <c>https://web.whatsapp.com/send?phone={tag}</c>. The host substitutes only the tag and
/// only after <see cref="TagPattern"/> matches it, so a hostile page cannot steer the URL.
/// </summary>
public sealed class NotificationActivation
{
    public string UrlTemplate { get; set; } = string.Empty;

    /// <summary>Regex the tag must match; null means "any non-empty tag".</summary>
    public string? TagPattern { get; set; }
}
