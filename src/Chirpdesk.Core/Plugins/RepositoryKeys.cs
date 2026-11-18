namespace Chirpdesk.Core.Plugins;

/// <summary>
/// Build-time trust anchors for repositories. The official key is pinned here; community repos
/// store their own first-use key in settings and are blocked if it ever changes (ADR 0008).
/// </summary>
public static class RepositoryKeys
{
    public const string OfficialFingerprint = "ecdsa-p256:569dd2be908bd358";

    public const string OfficialPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE3v4bz+lM5rxJE7nQ7UtSQ5x3uWhk
        UlLIJSh3n/I0pcMb87vCrMk8/tQw4mcHayv3NfxBb4eFbssa8xOpvnTyZw==
        -----END PUBLIC KEY-----
        """;
}
