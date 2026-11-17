using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "chirpdesk-tests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsStore CreateStore() => new(SettingsPath, NullLog.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Round_trips_a_custom_site()
    {
        var store = CreateStore();
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance
        {
            InstanceId = "my-chat",
            DefinitionId = null,
            Name = "My Chat",
            Url = "https://chat.example.com/",
            WarmAtBoot = true,
        });

        store.Save(settings);
        var loaded = store.Load();

        var instance = Assert.Single(loaded.Services);
        Assert.Equal("my-chat", instance.InstanceId);
        Assert.Null(instance.DefinitionId);
        Assert.Equal("https://chat.example.com/", instance.Url);
        Assert.True(instance.WarmAtBoot);
        Assert.Equal("my-chat", instance.ProfileId);
    }

    [Fact]
    public void A_missing_file_returns_defaults()
    {
        var loaded = CreateStore().Load();
        Assert.Empty(loaded.Services);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void Corrupt_json_returns_defaults_and_keeps_a_copy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var loaded = CreateStore().Load();

        Assert.Empty(loaded.Services);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
        Assert.True(File.Exists(SettingsPath));
    }

    [Fact]
    public void An_interrupted_write_leaves_the_previous_file_intact()
    {
        var store = CreateStore();
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance { InstanceId = "kept", Name = "Kept", Url = "https://a.example/" });
        store.Save(settings);

        // A crash between writing the temp file and replacing the target leaves a stale .tmp.
        File.WriteAllText(SettingsPath + ".tmp", "{ half a writ");

        var loaded = store.Load();

        var instance = Assert.Single(loaded.Services);
        Assert.Equal("kept", instance.InstanceId);
    }

    [Fact]
    public void A_newer_schema_is_not_downgraded_or_destroyed()
    {
        Directory.CreateDirectory(_dir);
        var newer = """{ "schemaVersion": 999, "services": [] }""";
        File.WriteAllText(SettingsPath, newer);

        var loaded = CreateStore().Load();

        Assert.Empty(loaded.Services);
        Assert.Equal(newer, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".corrupt"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("Has-Uppercase")]
    [InlineData("-leading-dash")]
    [InlineData("has spaces")]
    [InlineData("0123456789012345678901234567890123")]
    public void An_invalid_instance_id_is_rejected_on_save(string id)
    {
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance { InstanceId = id, Name = "x", Url = "https://a.example/" });

        Assert.Throws<InvalidSettingsException>(() => CreateStore().Save(settings));
    }

    [Fact]
    public void Duplicate_instance_ids_are_rejected_on_save()
    {
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance { InstanceId = "same", Name = "a", Url = "https://a.example/" });
        settings.Services.Add(new ServiceInstance { InstanceId = "same", Name = "b", Url = "https://b.example/" });

        Assert.Throws<InvalidSettingsException>(() => CreateStore().Save(settings));
    }

    [Fact]
    public void A_site_without_a_definition_needs_a_url()
    {
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance { InstanceId = "no-url", DefinitionId = null, Name = "x" });

        Assert.Throws<InvalidSettingsException>(() => CreateStore().Save(settings));
    }

    [Fact]
    public void Invalid_settings_that_are_already_on_disk_fall_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "schemaVersion": 1, "services": [ { "instanceId": "BAD ID" } ] }""");

        Assert.Empty(CreateStore().Load().Services);
        Assert.True(File.Exists(SettingsPath + ".corrupt"));
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("off", "off")]
    [InlineData("nonsense", "off")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void Page_dark_mode_is_normalised_and_nulls_inherit(string? stored, string? expected)
    {
        var store = CreateStore();
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance
        {
            InstanceId = "site",
            Name = "Site",
            Url = "https://a.example/",
            PageDarkMode = stored!,
        });

        store.Save(settings);

        Assert.Equal(expected, Assert.Single(store.Load().Services).PageDarkMode);
    }

    [Fact]
    public void Saving_twice_keeps_the_latest_state()
    {
        var store = CreateStore();
        var settings = new AppSettings();
        settings.Services.Add(new ServiceInstance { InstanceId = "one", Name = "One", Url = "https://a.example/" });
        store.Save(settings);

        settings.Services[0].Name = "Renamed";
        store.Save(settings);

        Assert.Equal("Renamed", Assert.Single(store.Load().Services).Name);
    }
}
