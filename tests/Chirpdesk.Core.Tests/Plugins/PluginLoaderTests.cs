using Chirpdesk.Core.Plugins;

namespace Chirpdesk.Core.Tests.Plugins;

public sealed class PluginLoaderTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "chirpdesk-plugin-tests",
        Guid.NewGuid().ToString("N"));

    public PluginLoaderTests() => Directory.CreateDirectory(_sandbox);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_sandbox, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that refuses to go is not a test failure.
        }
    }

    [Fact]
    public void A_valid_service_plugin_takes_id_and_name_from_the_manifest()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat", name: "Example Chat"),
            """
            {
              "url": "https://chat.example.com/",
              "unreadRegex": "\\((\\d+)\\)",
              "pageDarkMode": "auto"
            }
            """);

        var result = Load(Bundle(bundled));

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("example-chat", definition.Id);
        Assert.Equal("Example Chat", definition.Name);
        Assert.Equal("https://chat.example.com/", definition.Url);
        Assert.Equal("auto", definition.PageDarkMode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_definition_id_that_disagrees_with_the_manifest_is_skipped()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat"),
            ServiceJson(id: "something-else", name: "Example Chat"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("does not match", StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_plugin_without_a_payload_is_skipped()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat", Manifest("example-chat"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("service.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_manifest_with_an_invalid_id_is_skipped()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "Bad Folder",
            Manifest("Not A Valid Id"),
            ServiceJson(id: "Not A Valid Id", name: "Bad"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_newer_api_version_is_skipped_with_a_clear_reason()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat", apiVersion: 2),
            ServiceJson(id: "example-chat", name: "Example Chat"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("apiVersion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_permissions_disable_the_plugin()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat", permissions: """["telepathy"]"""),
            ServiceJson(id: "example-chat", name: "Example Chat"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("telepathy", StringComparison.Ordinal));
    }

    [Fact]
    public void Page_javascript_is_refused_even_when_declared()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat", permissions: """["inject-js"]"""),
            ServiceJson(id: "example-chat", name: "Example Chat"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("inject-js", StringComparison.Ordinal));
    }

    [Fact]
    public void Bundled_only_permissions_are_refused_for_user_plugins()
    {
        var user = Root("user");
        WritePlugin(user, "tool-cleaner",
            Manifest("tool-cleaner", permissions: """["trim-cache"]"""),
            ServiceJson(id: "tool-cleaner", name: "Tool"));

        var result = Load(User(user));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("bundled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_user_plugin_shadows_a_bundled_plugin_with_the_same_id()
    {
        var bundled = Root("bundled");
        var user = Root("user");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat", name: "Bundled"),
            ServiceJson(id: "example-chat", name: "Bundled", url: "https://bundled.example.com/"));
        WritePlugin(user, "example-chat",
            Manifest("example-chat", name: "From user"),
            ServiceJson(id: "example-chat", name: "From user", url: "https://user.example.com/"));

        var result = Load(User(user), Bundle(bundled));

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("https://user.example.com/", definition.Url);
        Assert.Contains("example-chat", result.Shadowed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Duplicate_ids_inside_one_tier_keep_the_first_and_report_the_second()
    {
        var bundled = Root("bundled");
        var first = Path.Combine(bundled, "a-first");
        var second = Path.Combine(bundled, "b-second");
        WritePluginAt(first, Manifest("example-chat", name: "First"), ServiceJson(id: "example-chat", name: "First", url: "https://first.example.com/"));
        WritePluginAt(second, Manifest("example-chat", name: "Second"), ServiceJson(id: "example-chat", name: "Second", url: "https://second.example.com/"));

        var result = Load(Bundle(bundled));

        Assert.Equal("https://first.example.com/", Assert.Single(result.Definitions).Url);
        Assert.Contains(result.Errors, e => e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_unknown_kind_is_reported_and_skipped()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "mystery", Manifest("mystery", kind: "template"), serviceJson: null);

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("template", StringComparison.Ordinal));
    }

    [Fact]
    public void A_known_non_service_kind_is_ignored_quietly()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "theme-catppuccin", Manifest("theme-catppuccin", kind: "theme", name: "Catppuccin"), serviceJson: null);

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void A_missing_plugin_root_is_not_an_error()
    {
        var result = Load(new PluginScanRoot(Path.Combine(_sandbox, "does-not-exist"), Bundled: true));

        Assert.Empty(result.Definitions);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Malformed_manifest_json_is_skipped_and_reported()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "broken", "{ not json", serviceJson: null);

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void A_traversing_icon_is_rejected_by_the_definition_validator()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            Manifest("example-chat"),
            ServiceJson(id: "example-chat", name: "Example Chat", icon: "../icon.svg"));

        var result = Load(Bundle(bundled));

        Assert.Empty(result.Definitions);
        Assert.Contains(result.Errors, e => e.Contains("icon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_permissions_are_allowed()
    {
        var bundled = Root("bundled");
        WritePlugin(bundled, "example-chat",
            """
            { "id": "example-chat", "kind": "service", "name": "Example Chat", "version": "1.0.0", "apiVersion": 1 }
            """,
            ServiceJson(id: "example-chat", name: "Example Chat"));

        var result = Load(Bundle(bundled));

        Assert.Single(result.Definitions);
        Assert.Empty(result.Errors);
    }

    private static PluginScanRoot Bundle(string directory) => new(directory, Bundled: true);

    private static PluginScanRoot User(string directory) => new(directory, Bundled: false);

    private static PluginLoadResult Load(params PluginScanRoot[] roots) =>
        PluginLoader.LoadServiceDefinitions(roots);

    private string Root(string name)
    {
        var directory = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WritePlugin(string root, string folder, string manifest, string? serviceJson = null) =>
        WritePluginAt(Path.Combine(root, folder), manifest, serviceJson);

    private static void WritePluginAt(string folder, string manifest, string? serviceJson)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest);

        if (serviceJson is not null)
        {
            File.WriteAllText(Path.Combine(folder, "service.json"), serviceJson);
        }
    }

    private static string Manifest(
        string id,
        string kind = "service",
        string name = "Example Chat",
        string version = "1.0.0",
        int apiVersion = 1,
        string permissions = "[]") =>
        $$"""
        {
          "id": "{{id}}",
          "kind": "{{kind}}",
          "name": "{{name}}",
          "version": "{{version}}",
          "apiVersion": {{apiVersion}},
          "permissions": {{permissions}}
        }
        """;

    private static string ServiceJson(
        string? id = null,
        string? name = null,
        string url = "https://chat.example.com/",
        string? icon = null)
    {
        var fields = new List<string>
        {
            $"\"url\": \"{url}\"",
            "\"unreadRegex\": \"\\\\((\\\\d+)\\\\)\"",
            "\"pageDarkMode\": \"auto\"",
        };

        if (id is not null)
        {
            fields.Insert(0, $"\"id\": \"{id}\"");
        }

        if (name is not null)
        {
            fields.Insert(id is null ? 0 : 1, $"\"name\": \"{name}\"");
        }

        if (icon is not null)
        {
            fields.Add($"\"icon\": \"{icon}\"");
        }

        return "{\n  " + string.Join(",\n  ", fields) + "\n}";
    }
}
