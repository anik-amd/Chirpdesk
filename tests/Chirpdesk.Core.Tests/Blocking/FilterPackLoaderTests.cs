using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Tests.Blocking;

public sealed class FilterPackLoaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "chirpdesk-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void WritePlugin(string folder, string id, string filtersJson, string kind = "filter")
    {
        var path = Path.Combine(_dir, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "kind": "{{kind}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "apiVersion": 1,
              "author": "test",
              "permissions": ["filters"]
            }
            """);
        File.WriteAllText(Path.Combine(path, "filters.json"), filtersJson);
    }

    [Fact]
    public void A_valid_filter_plugin_is_loaded_with_its_manifest_id()
    {
        WritePlugin("filters-core", "filters-core", """
            {
              "blockHosts": ["ads.example.com"],
              "blockPaths": ["*/tr/"],
              "allowHosts": ["safe.example.com"],
              "cosmetic": [".sponsored"]
            }
            """);

        var packs = PluginLoader.LoadFilters([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance);

        var pack = Assert.Single(packs);
        Assert.Equal("filters-core", pack.Id);
        Assert.Equal(["ads.example.com"], pack.BlockHosts);
        Assert.Equal([".sponsored"], pack.Cosmetic);
    }

    [Fact]
    public void A_broken_pack_is_skipped_and_other_packs_still_load()
    {
        WritePlugin("broken", "broken", "{ not json");
        WritePlugin("good", "good", """{ "blockHosts": ["ads.example.com"] }""");

        var packs = PluginLoader.LoadFilters([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance);

        Assert.Equal("good", Assert.Single(packs).Id);
    }

    [Fact]
    public void Non_filter_plugins_are_ignored()
    {
        WritePlugin("theme", "theme", """{ "blockHosts": ["ads.example.com"] }""", kind: "theme");

        Assert.Empty(PluginLoader.LoadFilters([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance));
    }

    [Fact]
    public void A_pack_without_filters_json_is_skipped()
    {
        var path = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "plugin.json"), """
            {
              "id": "empty",
              "kind": "filter",
              "name": "empty",
              "version": "1.0.0",
              "apiVersion": 1,
              "author": "test"
            }
            """);

        Assert.Empty(PluginLoader.LoadFilters([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance));
    }
}
