using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Chirpdesk.Core.Updates;

/// <summary>
/// One release manifest. The signature covers <see cref="UpdateManifestCodec.CanonicalBytes"/>
/// and is deliberately not part of that payload, so a signer never signs its own signature.
/// </summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = string.Empty;

    /// <summary>Only <c>stable</c> is accepted by the running app in v1.</summary>
    public string Channel { get; set; } = "stable";

    /// <summary>Absolute <c>https</c> download URL for the installer.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Exact payload length in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Lowercase or uppercase hex SHA-256 of the installer.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Base64 of the fixed 64-byte P-256 IEEE P1363 signature.</summary>
    public string Signature { get; set; } = string.Empty;
}

/// <summary>
/// Reads a manifest and produces the bytes the release key signs. The canonical form is fixed
/// key order, no whitespace and relaxed escaping, so release tooling can reproduce it without
/// depending on .NET's serializer defaults.
/// </summary>
public static class UpdateManifestCodec
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static UpdateManifest? TryRead(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "update manifest is empty";
            return null;
        }

        UpdateManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            error = $"update manifest is not valid json: {ex.Message}";
            return null;
        }

        if (manifest is null)
        {
            error = "update manifest is null";
            return null;
        }

        manifest.Version = manifest.Version?.Trim() ?? string.Empty;
        manifest.Channel = string.IsNullOrWhiteSpace(manifest.Channel)
            ? "stable"
            : manifest.Channel.Trim();
        manifest.Url = manifest.Url?.Trim() ?? string.Empty;
        manifest.Sha256 = manifest.Sha256?.Trim() ?? string.Empty;
        manifest.Signature = manifest.Signature?.Trim() ?? string.Empty;

        if (manifest.Version.Length == 0
            || manifest.Url.Length == 0
            || manifest.Sha256.Length == 0
            || manifest.Signature.Length == 0)
        {
            error = "update manifest is missing version, url, sha256 or signature";
            return null;
        }

        if (manifest.Size <= 0)
        {
            error = "update manifest size must be positive";
            return null;
        }

        return manifest;
    }

    /// <summary>
    /// The signed bytes: version, channel, url, size and sha256 in that order. Signature is not
    /// present, so signing the same manifest twice produces the same bytes.
    /// </summary>
    public static byte[] CanonicalBytes(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("version", manifest.Version);
            writer.WriteString("channel", manifest.Channel);
            writer.WriteString("url", manifest.Url);
            writer.WriteNumber("size", manifest.Size);
            writer.WriteString("sha256", manifest.Sha256);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Whitespace-free UTF-8 of <see cref="CanonicalBytes"/> for release tooling.</summary>
    public static string CanonicalJson(UpdateManifest manifest) =>
        Encoding.UTF8.GetString(CanonicalBytes(manifest));
}
