using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chirpdesk.Core.Updates;

namespace Chirpdesk.Core.Tests.Updates;

public sealed class UpdateVerifierTests : IDisposable
{
    private const string InstallerUrl =
        "https://github.com/anik-amd/Chirpdesk/releases/download/v1.2.0/Chirpdesk-Setup.exe";

    private const string PayloadSha256 =
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "chirpdesk-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void A_signed_newer_stable_https_manifest_is_accepted()
    {
        using var key = NewKey();
        var manifest = Signed(key);

        Assert.True(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out var error),
            error);
    }

    [Fact]
    public void A_tampered_field_is_refused()
    {
        using var key = NewKey();
        var manifest = Signed(key);
        manifest.Url = InstallerUrl.Replace("v1.2.0", "v9.9.9", StringComparison.Ordinal);

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out _));
    }

    [Fact]
    public void A_signature_from_another_key_is_refused()
    {
        using var signer = NewKey();
        using var other = NewKey();
        var manifest = Signed(signer);

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, other.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out _));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.9.9")]
    public void A_version_that_is_not_newer_is_refused(string version)
    {
        using var key = NewKey();
        var manifest = Signed(key, version);

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out var error));
        Assert.Contains("newer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_beta_channel_is_refused()
    {
        using var key = NewKey();
        var manifest = Signed(key);
        manifest.Channel = "beta";
        manifest.Signature = Sign(key, manifest);

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out var error));
        Assert.Contains("stable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unparseable_version_is_refused()
    {
        using var key = NewKey();
        var manifest = Signed(key, "1.2.0-beta.1");

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out _));
    }

    [Theory]
    [InlineData("http://example.org/setup.exe")]
    [InlineData("https://")]
    [InlineData("setup.exe")]
    public void A_download_url_that_is_not_absolute_https_is_refused(string url)
    {
        using var key = NewKey();
        var manifest = Signed(key);
        manifest.Url = url;
        manifest.Signature = Sign(key, manifest);

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out var error));
        Assert.Contains("https", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not base64")]
    [InlineData("AAAA")]
    public void A_malformed_signature_is_refused(string signature)
    {
        using var key = NewKey();
        var manifest = Signed(key);
        manifest.Signature = signature;

        Assert.False(
            UpdateVerifier.IsAcceptable(manifest, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_missing_public_key_is_refused()
    {
        using var key = NewKey();
        var manifest = Signed(key);

        Assert.False(UpdateVerifier.IsAcceptable(manifest, "not a pem", new Version(1, 0, 0, 0), out _));
    }

    [Fact]
    public void A_payload_with_the_expected_hash_and_size_is_accepted()
    {
        var manifest = new UpdateManifest
        {
            Version = "1.2.0",
            Url = InstallerUrl,
            Size = 3,
            Sha256 = PayloadSha256,
        };

        Assert.True(UpdateVerifier.VerifyPayload(manifest, WritePayload("abc"), out var error), error);
    }

    [Fact]
    public void A_payload_with_the_wrong_hash_or_size_is_refused()
    {
        var wrongHash = new UpdateManifest
        {
            Version = "1.2.0",
            Url = InstallerUrl,
            Size = 3,
            Sha256 = new string('0', 64),
        };

        var wrongSize = new UpdateManifest
        {
            Version = "1.2.0",
            Url = InstallerUrl,
            Size = 4,
            Sha256 = PayloadSha256,
        };

        var path = WritePayload("abc");

        Assert.False(UpdateVerifier.VerifyPayload(wrongHash, path, out _));
        Assert.False(UpdateVerifier.VerifyPayload(wrongSize, path, out _));
        Assert.False(UpdateVerifier.VerifyPayload(wrongHash, path + ".missing", out _));
    }

    [Fact]
    public void Canonical_bytes_do_not_change_when_only_the_signature_changes()
    {
        var manifest = new UpdateManifest
        {
            Version = "1.2.0",
            Url = InstallerUrl,
            Size = 3,
            Sha256 = PayloadSha256,
            Signature = "first",
        };

        var before = UpdateManifestCodec.CanonicalBytes(manifest);
        manifest.Signature = "second";

        Assert.Equal(before, UpdateManifestCodec.CanonicalBytes(manifest));
        Assert.DoesNotContain("signature", Encoding.UTF8.GetString(before), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Json_reading_ignores_unknown_fields_and_requires_the_contract()
    {
        const string json = """
            {
              "version": "1.2.0",
              "channel": "stable",
              "url": "https://example.org/setup.exe",
              "size": 3,
              "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
              "signature": "abc",
              "futureField": { "anything": true }
            }
            """;

        Assert.NotNull(UpdateManifestCodec.TryRead(json, out var error));
        Assert.Null(error);

        Assert.Null(UpdateManifestCodec.TryRead("{ not json", out var badJson));
        Assert.NotNull(badJson);

        Assert.Null(UpdateManifestCodec.TryRead("""{ "version": "1.2.0" }""", out var missing));
        Assert.NotNull(missing);
    }

    [Fact]
    public void The_manifest_round_trips_through_json()
    {
        using var key = NewKey();
        var manifest = Signed(key);
        var json = JsonSerializer.Serialize(manifest);

        var parsed = UpdateManifestCodec.TryRead(json, out var error);

        Assert.NotNull(parsed);
        Assert.Null(error);
        Assert.Equal(manifest.Signature, parsed.Signature);
        Assert.True(
            UpdateVerifier.IsAcceptable(parsed, key.ExportSubjectPublicKeyInfoPem(), new Version(1, 0, 0, 0), out _));
    }

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static UpdateManifest Signed(ECDsa key, string version = "1.2.0")
    {
        var manifest = new UpdateManifest
        {
            Version = version,
            Channel = "stable",
            Url = InstallerUrl,
            Size = 3,
            Sha256 = PayloadSha256,
        };

        manifest.Signature = Sign(key, manifest);
        return manifest;
    }

    private static string Sign(ECDsa key, UpdateManifest manifest) =>
        Convert.ToBase64String(key.SignData(
            UpdateManifestCodec.CanonicalBytes(manifest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private string WritePayload(string text)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "Chirpdesk-Setup.exe");
        File.WriteAllText(path, text);
        return path;
    }
}
