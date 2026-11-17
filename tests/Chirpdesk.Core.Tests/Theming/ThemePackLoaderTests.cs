using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Tests.Theming;

public sealed class ThemePackLoaderTests : IDisposable
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

    [Fact]
    public void A_theme_pack_loads_its_flavours()
    {
        WriteTheme("catppuccin", """
            {
              "flavors": [
                { "id": "latte", "name": "Latte", "mode": "light", "swatch": "#EFF1F5" },
                { "id": "mocha", "name": "Mocha", "mode": "dark", "swatch": "#1E1E2E" }
              ]
            }
            """);

        var pack = Assert.Single(PluginLoader.LoadThemes([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance));

        Assert.Equal("catppuccin", pack.Id);
        Assert.Equal(2, pack.Flavors.Count);
        Assert.Contains(pack.Flavors, flavor => flavor.Id == "mocha" && flavor.Mode == "dark");
    }

    [Fact]
    public void A_bad_flavour_is_dropped_and_the_rest_load()
    {
        WriteTheme("mixed", """
            {
              "flavors": [
                { "id": "bad id", "name": "Bad", "mode": "dark", "swatch": "#000000" },
                { "id": "ok", "name": "Okay", "mode": "dark", "swatch": "#111111" }
              ]
            }
            """);

        var pack = Assert.Single(PluginLoader.LoadThemes([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance));

        Assert.Equal("ok", Assert.Single(pack.Flavors).Id);
    }

    [Fact]
    public void A_theme_without_themes_json_is_a_capability_and_not_an_error()
    {
        var folder = Path.Combine(_dir, "page-dark");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), """
            {
              "id": "page-dark",
              "kind": "theme",
              "name": "Page dark",
              "version": "1.0.0",
              "apiVersion": 1,
              "author": "test"
            }
            """);

        Assert.Empty(PluginLoader.LoadThemes([new PluginScanRoot(_dir, Bundled: true)], NullLog.Instance));
    }

    private void WriteTheme(string folder, string themesJson)
    {
        var path = Path.Combine(_dir, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "plugin.json"), $$"""
            {
              "id": "{{folder}}",
              "kind": "theme",
              "name": "{{folder}}",
              "version": "1.0.0",
              "apiVersion": 1,
              "author": "test"
            }
            """);
        File.WriteAllText(Path.Combine(path, "themes.json"), themesJson);
    }
}
