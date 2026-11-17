using System.Security.Cryptography;

namespace Chirpdesk.Core.Updates;

/// <summary>
/// Verification only: no network, no installer launch and no state. Policy is fail closed - any
/// malformed value, wrong key, replay or hash mismatch returns false with a reason, and the
/// caller keeps running the current build (ADR 0006).
/// </summary>
public static class UpdateVerifier
{
    public static bool IsAcceptable(
        UpdateManifest manifest,
        string publicKeyPem,
        Version currentVersion,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentVersion);

        return IsAbsoluteHttps(manifest.Url, out error)
            && IsNewerStableRelease(manifest, currentVersion, out error)
            && VerifySignature(manifest, publicKeyPem, out error);
    }

    /// <summary>Absolute https only; a relative or plain-http URL is refused before any fetch.</summary>
    public static bool IsAbsoluteHttps(string? url, out string error)
    {
        error = string.Empty;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "installer URL must be absolute https";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Stable channel only, a plain numeric version, and strictly newer than the running build.
    /// A signed older release is still refused, so a valid key cannot replay a downgrade.
    /// </summary>
    public static bool IsNewerStableRelease(UpdateManifest manifest, Version currentVersion, out string error)
    {
        error = string.Empty;

        if (!string.Equals(manifest.Channel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            error = $"channel '{manifest.Channel}' is not stable";
            return false;
        }

        if (!Version.TryParse(manifest.Version, out var published))
        {
            error = $"version '{manifest.Version}' is not a plain numeric version";
            return false;
        }

        var current = Normalize(currentVersion);
        published = Normalize(published);

        if (published <= current)
        {
            error = $"{published} is not newer than {current}";
            return false;
        }

        return true;
    }

    public static bool VerifySignature(UpdateManifest manifest, string publicKeyPem, out string error)
    {
        error = string.Empty;

        byte[] signature;

        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException)
        {
            error = "signature is not base64";
            return false;
        }

        if (signature.Length != 64)
        {
            error = "signature is not a 64-byte P-256 signature";
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            if (key.KeySize != 256)
            {
                error = "update key is not P-256";
                return false;
            }

            if (!key.VerifyData(
                    UpdateManifestCodec.CanonicalBytes(manifest),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                error = "signature does not match the manifest";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            error = $"update key is invalid: {ex.Message}";
            return false;
        }

        return true;
    }

    /// <summary>Payload check after download: exact size and SHA-256 from the signed manifest.</summary>
    public static bool VerifyPayload(UpdateManifest manifest, string path, out string error)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        error = string.Empty;

        if (!IsSha256(manifest.Sha256))
        {
            error = "manifest sha256 is not 64 hex characters";
            return false;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            error = "downloaded installer is missing";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);

            if (stream.Length != manifest.Size)
            {
                error = $"installer size {stream.Length} does not match {manifest.Size}";
                return false;
            }

            var hash = Convert.ToHexString(SHA256.HashData(stream));

            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "installer sha256 does not match the manifest";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"could not read the installer: {ex.Message}";
            return false;
        }

        return true;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
