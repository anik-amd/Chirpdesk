using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Tests.Plugins;

public sealed class PluginStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "chirpdesk-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Bundled_plugins_are_active_until_explicitly_disabled()
    {
        var settings = new AppSettings();

        Assert.True(PluginState.IsEnabled(settings, PluginState.PageDarkId));
    }

    [Fact]
    public void Disabling_and_enabling_round_trips()
    {
        var settings = new AppSettings();

        PluginState.SetEnabled(settings, PluginState.PageDarkId, false);
        Assert.False(PluginState.IsEnabled(settings, PluginState.PageDarkId));

        PluginState.SetEnabled(settings, PluginState.PageDarkId, true);
        Assert.True(PluginState.IsEnabled(settings, PluginState.PageDarkId));
        Assert.Empty(settings.DisabledPlugins);
    }

    [Fact]
    public void Disabling_twice_does_not_duplicate_the_id()
    {
        var settings = new AppSettings();

        PluginState.SetEnabled(settings, PluginState.PageDarkId, false);
        PluginState.SetEnabled(settings, "PAGE-DARK", false);

        Assert.Equal(PluginState.PageDarkId, Assert.Single(settings.DisabledPlugins));
    }

    [Fact]
    public void An_invalid_disabled_id_is_dropped_rather_than_failing_settings()
    {
        var store = new SettingsStore(SettingsPath, NullLog.Instance);
        var settings = new AppSettings { DisabledPlugins = ["BAD ID", "page-dark"] };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(PluginState.PageDarkId, Assert.Single(loaded.DisabledPlugins));
        Assert.False(PluginState.IsEnabled(loaded, PluginState.PageDarkId));
    }
}

