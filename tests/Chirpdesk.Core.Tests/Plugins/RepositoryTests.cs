using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Tests.Plugins;

public sealed class RepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chirpdesk-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private const string IndexJson = """
        {
          "schema": 1,
          "name": "Chirpdesk Official",
          "generated": "2026-11-16T10:00:00Z",
          "keyFingerprint": "ecdsa-p256:0000000000000000",
          "plugins": [
            {
              "id": "filters-meta",
              "kind": "filter",
              "name": "Meta filters",
              "version": "1.4.0",
              "apiVersion": 1,
              "minAppVersion": "0.1.0",
              "sha256": "b1946ac92492d2347c6235b4d2611184c7d3a6d7a8c9b0c1d2e3f40506070809",
              "size": 18432,
              "url": "plugins/filters-meta-1.4.0.zip",
              "permissions": ["filters", "css"]
            }
          ]
        }
        """;

    [Fact]
    public void A_valid_index_is_read()
    {
        var index = RepositoryIndexCodec.TryRead(IndexJson, out var error);

        Assert.Null(error);
        Assert.NotNull(index);
        Assert.Equal("Chirpdesk Official", index.Name);
        Assert.Single(index.Plugins);
        Assert.Equal("filters-meta", index.Plugins[0].Id);
    }

    [Theory]
    [InlineData("../../evil.zip")]
    [InlineData("/absolute.zip")]
    [InlineData("C:/absolute.zip")]
    public void Unsafe_archive_urls_are_refused(string url)
    {
        var json = IndexJson.Replace("plugins/filters-meta-1.4.0.zip", url, StringComparison.Ordinal);

        Assert.Null(RepositoryIndexCodec.TryRead(json, out var error));
        Assert.Contains("url", error);
    }

    [Fact]
    public void An_unknown_api_version_is_unsupported()
    {
        var plugin = new RepositoryPlugin { ApiVersion = 2 };

        Assert.False(RepositoryCompatibility.IsCompatible(plugin, new Version(0, 1, 0), out var reason));
        Assert.Contains("API", reason);
    }

    [Fact]
    public void Integration_plugins_are_not_installable()
    {
        var plugin = new RepositoryPlugin { ApiVersion = 1, Kind = "integration" };

        Assert.False(RepositoryCompatibility.IsCompatible(plugin, new Version(0, 1, 0), out var reason));
        Assert.Contains("integration", reason);
    }

    [Fact]
    public void A_signed_index_verifies_and_a_tampered_one_does_not()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = Encoding.UTF8.GetBytes(IndexJson);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var pem = key.ExportSubjectPublicKeyInfoPem();

        Assert.True(RepositorySignature.Verify(bytes, Convert.ToBase64String(signature), pem, out var error), error);

        var tampered = Encoding.UTF8.GetBytes(IndexJson.Replace("1.4.0", "9.9.9", StringComparison.Ordinal));
        Assert.False(RepositorySignature.Verify(tampered, Convert.ToBase64String(signature), pem, out error));
    }

    [Fact]
    public void A_changed_key_has_a_different_fingerprint()
    {
        using var first = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var second = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var a = RepositorySignature.Fingerprint(first.ExportSubjectPublicKeyInfoPem());
        var b = RepositorySignature.Fingerprint(second.ExportSubjectPublicKeyInfoPem());

        Assert.NotEqual(a, b);
        Assert.StartsWith("ecdsa-p256:", a);
    }

    [Fact]
    public void Unsafe_archive_entries_and_executables_are_refused()
    {
        Assert.False(PluginArchive.IsSafeEntry("../plugin.json"));
        Assert.False(PluginArchive.IsSafeEntry("/absolute/plugin.json"));
        Assert.False(PluginArchive.IsSafeEntry("C:/plugin.json"));
        Assert.False(PluginArchive.IsSafeEntry("tool.exe"));
        Assert.True(PluginArchive.IsSafeEntry("plugin.json"));
        Assert.True(PluginArchive.IsSafeEntry("assets/screenshot.png"));
    }

    [Fact]
    public void A_plugin_source_round_trips_and_a_bad_file_fails_open()
    {
        Directory.CreateDirectory(_dir);
        var source = new PluginSource(
            "https://example.test/registry/",
            "ecdsa-p256:0000000000000000",
            "1.2.3",
            "b1946ac92492d2347c6235b4d2611184c7d3a6d7a8c9b0c1d2e3f40506070809",
            DateTimeOffset.UtcNow);

        Assert.True(PluginSourceStore.TryWrite(_dir, source));
        var read = PluginSourceStore.TryRead(_dir);
        Assert.NotNull(read);
        Assert.Equal(source.RepositoryUrl, read.RepositoryUrl);
        Assert.Equal(source.Version, read.Version);

        File.WriteAllText(Path.Combine(_dir, PluginSourceStore.FileName), "{ not json");
        Assert.Null(PluginSourceStore.TryRead(_dir));
    }

    [Fact]
    public void Extraction_stays_beneath_the_destination()
    {
        Directory.CreateDirectory(_dir);
        using var archive = new MemoryStream();

        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("plugin.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }

        archive.Position = 0;
        Assert.True(PluginArchive.TryExtract(archive, _dir, out var error), error);
        Assert.True(File.Exists(Path.Combine(_dir, "plugin.json")));
    }
}
