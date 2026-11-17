using Chirpdesk.Core.Definitions;

namespace Chirpdesk.Core.Tests.Definitions;

public class DefinitionLoaderTests
{
    private const string ValidJson = """
        {
          "id": "whatsapp",
          "name": "WhatsApp",
          "url": "https://web.whatsapp.com/",
          "notificationMode": "native",
          "pageDarkMode": "off",
          "unreadRegex": "\\((\\d+)\\)",
          "activation": { "urlTemplate": "https://web.whatsapp.com/send?phone={tag}" },
          "apiVersion": 1,
          "unknownFieldFromTheFuture": true
        }
        """;

    [Fact]
    public void Parses_a_valid_definition_and_ignores_unknown_fields()
    {
        var definition = DefinitionLoader.Parse(ValidJson, out var error);

        Assert.Null(error);
        Assert.NotNull(definition);
        Assert.Equal("whatsapp", definition!.Id);
        Assert.Equal("https://web.whatsapp.com/send?phone={tag}", definition.Activation!.UrlTemplate);
    }

    [Fact]
    public void Invalid_json_is_skipped_with_an_error()
    {
        Assert.Null(DefinitionLoader.Parse("{ not json", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void An_invalid_definition_is_skipped_with_its_errors()
    {
        var json = ValidJson.Replace("\"https://web.whatsapp.com/\"", "\"http://insecure/\"", StringComparison.Ordinal);

        Assert.Null(DefinitionLoader.Parse(json, out var error));
        Assert.Contains("invalid url", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Loading_many_keeps_the_valid_ones_and_reports_the_rest()
    {
        var errors = new List<string>();

        var definitions = DefinitionLoader.Load(
            [ValidJson, "{ broken", ValidJson.Replace("\"whatsapp\"", "\"other\"", StringComparison.Ordinal)],
            errors);

        Assert.Equal(2, definitions.Count);
        Assert.Single(errors);
    }

    [Fact]
    public void A_duplicate_id_is_rejected_rather_than_replaced()
    {
        var errors = new List<string>();

        var definitions = DefinitionLoader.Load([ValidJson, ValidJson], errors);

        Assert.Single(definitions);
        Assert.Contains("duplicate", Assert.Single(errors), StringComparison.Ordinal);
    }
}
