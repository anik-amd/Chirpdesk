using System.Security.Cryptography;

namespace Chirpdesk.Core.Plugins;

/// <summary>
/// ECDSA P-256 over the exact index bytes. Pinning is a fingerprint of the SPKI DER, not a
/// display name, so a changed key is always visible and blocking.
/// </summary>
public static class RepositorySignature
{
    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);

        var der = key.ExportSubjectPublicKeyInfo();
        return "ecdsa-p256:" + Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant()[..16];
    }

    public static bool Verify(
        ReadOnlySpan<byte> indexBytes,
        string? signatureBase64,
        string publicKeyPem,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(signatureBase64))
        {
            error = "repository signature is empty";
            return false;
        }

        byte[] signature;

        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            error = "repository signature is not base64";
            return false;
        }

        if (signature.Length != 64)
        {
            error = "repository signature is not a 64-byte P-256 signature";
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);

            if (key.KeySize != 256)
            {
                error = "repository key is not P-256";
                return false;
            }

            if (!key.VerifyData(
                    indexBytes,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                error = "repository signature does not match the index";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            error = $"repository key is invalid: {ex.Message}";
            return false;
        }

        return true;
    }

    public static bool MatchesPinnedKey(string publicKeyPem, string? expectedFingerprint, out string actualFingerprint)
    {
        actualFingerprint = Fingerprint(publicKeyPem);
        return string.Equals(actualFingerprint, expectedFingerprint, StringComparison.OrdinalIgnoreCase);
    }
}
