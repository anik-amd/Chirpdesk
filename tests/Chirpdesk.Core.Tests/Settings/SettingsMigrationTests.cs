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
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
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
    public void A_v2_file_is_left_alone()
    {
        Directory.CreateDirectory(_dir);
        var original = """
            { "schemaVersion": 2, "services": [] }
            """;
        File.WriteAllText(SettingsPath, original);

        _ = new SettingsStore(SettingsPath, NullLog.Instance).Load();

        Assert.Equal(original, File.ReadAllText(SettingsPath));
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
