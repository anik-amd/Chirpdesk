using Chirpdesk.Core.Integrations;
using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Tests.Integrations;

public sealed class IntegrationManifestTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "chirpdesk-integration-tests",
        Guid.NewGuid().ToString("N"));

    public IntegrationManifestTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_valid_manifest_is_read()
    {
        var manifest = IntegrationManifestCodec.TryRead(ValidJson, out var error);

        Assert.Null(error);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.ProtocolVersion);
        Assert.Equal("helper.exe", manifest.Executable);
        Assert.Equal(2, manifest.Commands.Count);
        Assert.Equal("text", manifest.Commands[1].Argument);
    }

    [Fact]
    public void An_unsupported_protocol_is_refused()
    {
        var manifest = IntegrationManifestCodec.TryRead(
            ValidJson.Replace("\"protocolVersion\": 1", "\"protocolVersion\": 2", StringComparison.Ordinal),
            out var error);

        Assert.Null(manifest);
        Assert.Contains("protocolVersion", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("helper.dll")]
    [InlineData("../helper.exe")]
    [InlineData("sub/helper.exe")]
    [InlineData("sub\\helper.exe")]
    [InlineData("C:\\helper.exe")]
    [InlineData("helper.exe:stream")]
    [InlineData("")]
    public void The_executable_must_be_a_plain_exe_name(string name)
    {
        Assert.False(IntegrationManifestCodec.IsPlainExecutableName(name));
    }

    [Fact]
    public void Duplicate_command_ids_are_refused()
    {
        var json = ValidJson.Replace(
            "\"id\": \"send\"",
            "\"id\": \"open\"",
            StringComparison.Ordinal);

        var manifest = IntegrationManifestCodec.TryRead(json, out var error);

        Assert.Null(manifest);
        Assert.Contains("duplicate command id", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_argument_mode_is_refused()
    {
        var json = ValidJson.Replace("\"argument\": \"text\"", "\"argument\": \"file\"", StringComparison.Ordinal);

        var manifest = IntegrationManifestCodec.TryRead(json, out var error);

        Assert.Null(manifest);
        Assert.Contains("argument mode", error, StringComparison.Ordinal);
    }

    [Fact]
    public void More_than_twenty_commands_are_refused()
    {
        var commands = string.Join(",", Enumerable.Range(0, 21).Select(index =>
            $$"""{"id":"command-{{index}}","title":"Command {{index}}","argument":"none"}"""));
        var json = $$"""{"protocolVersion":1,"executable":"helper.exe","commands":[{{commands}}]}""";

        var manifest = IntegrationManifestCodec.TryRead(json, out var error);

        Assert.Null(manifest);
        Assert.Contains("at most 20", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_folder_without_integration_json_is_refused()
    {
        var folder = Folder("empty");

        var manifest = IntegrationLoader.Read(folder, out var errors);

        Assert.Null(manifest);
        Assert.Contains(errors, item => item.Contains("integration.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_executable_is_refused()
    {
        var folder = Folder("missing-exe");
        File.WriteAllText(Path.Combine(folder, "integration.json"), ValidJson);

        var manifest = IntegrationLoader.Read(folder, out var errors);

        Assert.Null(manifest);
        Assert.Contains(errors, item => item.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_executable_is_refused()
    {
        var folder = Folder("empty-exe");
        File.WriteAllText(Path.Combine(folder, "integration.json"), ValidJson);
        File.WriteAllBytes(Path.Combine(folder, "helper.exe"), []);

        var manifest = IntegrationLoader.Read(folder, out var errors);

        Assert.Null(manifest);
        Assert.Contains(errors, item => item.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public void The_install_validator_pins_the_executable_hash()
    {
        var folder = Folder("install");
        File.WriteAllText(Path.Combine(folder, "integration.json"), ValidJson);
        File.WriteAllBytes(Path.Combine(folder, "helper.exe"), [0x4D, 0x5A, 0x01]);

        var entry = new RepositoryPlugin { Id = "telegram-tdlib", Kind = "integration", Name = "Telegram" };

        Assert.True(
            IntegrationInstallValidator.TryValidate(folder, entry, out var hash, out var error),
            error);
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(0, hash.Trim('0').Length);
    }

    [Fact]
    public void The_install_validator_requires_integration_json()
    {
        var folder = Folder("install-missing");
        var entry = new RepositoryPlugin { Id = "telegram-tdlib", Kind = "integration", Name = "Telegram" };

        Assert.False(IntegrationInstallValidator.TryValidate(folder, entry, out _, out var error));
        Assert.Contains("integration.json", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_install_validator_refuses_a_foreign_kind()
    {
        var folder = Folder("install-kind");
        var entry = new RepositoryPlugin { Id = "telegram-tdlib", Kind = "theme", Name = "Theme" };

        Assert.False(IntegrationInstallValidator.TryValidate(folder, entry, out _, out var error));
        Assert.Contains("not an integration", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_folder_is_read()
    {
        var folder = Folder("complete");
        File.WriteAllText(Path.Combine(folder, "integration.json"), ValidJson);
        File.WriteAllBytes(Path.Combine(folder, "helper.exe"), [0x4D, 0x5A]);

        var manifest = IntegrationLoader.Read(folder, out var errors);

        Assert.Empty(errors);
        Assert.NotNull(manifest);
        Assert.Equal("helper.exe", manifest.Executable);
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private const string ValidJson = """
        {
          "protocolVersion": 1,
          "executable": "helper.exe",
          "commands": [
            {
              "id": "open",
              "title": "Open Telegram",
              "description": "Bring the helper window forward",
              "argument": "none"
            },
            {
              "id": "send",
              "title": "Telegram: send",
              "argument": "text"
            }
          ]
        }
        """;
}
