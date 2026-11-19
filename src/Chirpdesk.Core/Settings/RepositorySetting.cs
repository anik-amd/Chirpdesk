namespace Chirpdesk.Core.Settings;

/// <summary>
/// One HTTPS plugin repository. The key is pinned on first successful sync; a later key change
/// blocks the repo until the user explicitly re-trusts the new fingerprint (ADR 0008).
/// </summary>
public sealed class RepositorySetting
{
    public string Url { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string PublicKeyPem { get; set; } = string.Empty;

    public string KeyFingerprint { get; set; } = string.Empty;

    public bool AutoUpdate { get; set; } = true;

    public DateTimeOffset? LastSync { get; set; }
}
