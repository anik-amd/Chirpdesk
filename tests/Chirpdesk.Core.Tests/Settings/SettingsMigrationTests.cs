using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Tests.Settings;

public sealed class SettingsMigrationTests : IDisposable
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
    public void A_v1_file_is_migrated_and_its_custom_instance_survives()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 1,
              "services": [
                {
                  "instanceId": "my-chat",
                  "definitionId": null,
                  "name": "My Chat",
                  "url": "https://chat.example.com/",
                  "warmAtBoot": true
                }
              ]
            }
            """);

        var loaded = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        var instance = Assert.Single(loaded.Services);
        Assert.Equal("my-chat", instance.InstanceId);
        Assert.Equal("https://chat.example.com/", instance.Url);

        // Migration is written back, not only held in memory.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(AppSettings.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Migration_fills_the_new_defaults_without_inventing_instances()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            { "schemaVersion": 1, "services": [] }
            """);

        var loaded = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Empty(loaded.Services);
        Assert.Equal("system", loaded.Theme);
    }

    [Fact]
    public void A_v2_file_migrates_to_the_current_schema_without_losing_instances()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 2,
              "services": [
                {
                  "instanceId": "whatsapp",
                  "definitionId": "whatsapp",
                  "name": "WhatsApp",
                  "warmAtBoot": true
                }
              ]
            }
            """);

        var loaded = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Equal("whatsapp", Assert.Single(loaded.Services).InstanceId);
        Assert.Equal(60, loaded.CacheBudgetMb);
        Assert.Equal(180, loaded.GlobalCacheBudgetMb);
        Assert.Equal(0, loaded.ReclaimedBytes);
        Assert.False(loaded.Autostart);
        Assert.Null(loaded.DndUntil);

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal(AppSettings.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void A_v3_file_pins_existing_services_for_auto_warmup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 3,
              "services": [
                {
                  "instanceId": "whatsapp",
                  "definitionId": "whatsapp",
                  "name": "WhatsApp",
                  "warmAtBoot": false
                }
              ]
            }
            """);

        var loaded = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.True(Assert.Single(loaded.Services).WarmAtBoot);
    }

    [Fact]
    public void A_v4_file_is_left_alone()
    {
        Directory.CreateDirectory(_dir);
        var original = """
            { "schemaVersion": 4, "services": [] }
            """;
        File.WriteAllText(SettingsPath, original);

        _ = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Equal(original, File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Budgets_are_clamped_and_global_never_smaller_than_per_profile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "schemaVersion": 3,
              "services": [],
              "cacheBudgetMb": 500,
              "globalCacheBudgetMb": 100,
              "reclaimedBytes": -10
            }
            """);

        var loaded = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Equal(500, loaded.CacheBudgetMb);
        Assert.Equal(500, loaded.GlobalCacheBudgetMb);
        Assert.Equal(0, loaded.ReclaimedBytes);
    }

    [Fact]
    public void Dnd_until_round_trips()
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(SettingsPath, NullLog.Instance);
        var settings = new AppSettings
        {
            DndUntil = DateTimeOffset.UtcNow.AddMinutes(30),
            Autostart = true,
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(settings.DndUntil, loaded.DndUntil);
        Assert.True(loaded.Autostart);
    }

    [Fact]
    public void A_newer_schema_is_still_not_downgraded()
    {
        Directory.CreateDirectory(_dir);
        var newer = """{ "schemaVersion": 99, "services": [] }""";
        File.WriteAllText(SettingsPath, newer);

        var loaded = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Empty(loaded.Services);
        Assert.Equal(newer, File.ReadAllText(SettingsPath));
    }
}
