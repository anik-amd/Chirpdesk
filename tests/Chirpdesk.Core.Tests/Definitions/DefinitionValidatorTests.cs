using Chirpdesk.Core.Definitions;

namespace Chirpdesk.Core.Tests.Definitions;

public class DefinitionValidatorTests
{
    private static ServiceDefinition Valid() => new()
    {
        Id = "whatsapp",
        Name = "WhatsApp",
        Url = "https://web.whatsapp.com/",
        NotificationMode = ServiceDefinition.NativeMode,
        PageDarkMode = ServiceDefinition.PageDarkOff,
    };

    [Fact]
    public void A_minimal_definition_is_valid()
    {
        var errors = DefinitionValidator.Validate(Valid());
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("WhatsApp")]
    [InlineData("-leading")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("0123456789012345678901234567890123")]
    public void An_invalid_id_is_rejected(string id)
    {
        var definition = Valid();
        definition.Id = id;

        // No Normalize here on purpose: validation must reject a raw, non-canonical id, and
        // normalization is what turns an acceptable one into lower case.
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("invalid id", StringComparison.Ordinal));
    }

    [Fact]
    public void A_missing_name_is_rejected()
    {
        var definition = Valid();
        definition.Name = "  ";
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("no name", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://web.whatsapp.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@web.whatsapp.com/")]
    [InlineData("not a url")]
    public void A_non_https_or_credentialed_url_is_rejected(string url)
    {
        var definition = Valid();
        definition.Url = url;
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("invalid url", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../icon.png")]
    [InlineData("sub/icon.png")]
    [InlineData("sub\\icon.png")]
    public void An_icon_must_be_a_file_name(string icon)
    {
        var definition = Valid();
        definition.Icon = icon;
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("icon", StringComparison.Ordinal));
    }

    [Fact]
    public void An_icon_file_name_is_accepted()
    {
        var definition = Valid();
        definition.Icon = "whatsapp.png";
        Assert.Empty(DefinitionValidator.Validate(definition));
    }

    [Fact]
    public void An_unknown_notification_mode_is_rejected()
    {
        var definition = Valid();
        definition.NotificationMode = "magic";
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("notification mode", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_page_dark_mode_is_rejected()
    {
        var definition = Valid();
        definition.PageDarkMode = "force";
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("page dark mode", StringComparison.Ordinal));
    }

    [Fact]
    public void A_broken_unread_regex_is_rejected()
    {
        var definition = Valid();
        definition.UnreadRegex = "(\\d+";
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("unread regex", StringComparison.Ordinal));
    }

    [Fact]
    public void An_api_version_newer_than_the_host_is_rejected()
    {
        var definition = Valid();
        definition.ApiVersion = DefinitionValidator.HostApiVersion + 1;
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("apiVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void MinApiVersion_above_ApiVersion_is_rejected()
    {
        var definition = Valid();
        definition.MinApiVersion = 9;
        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("minApiVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void An_activation_template_needs_a_tag_and_https()
    {
        var definition = Valid();
        definition.Activation = new NotificationActivation { UrlTemplate = "http://example.com/chat" };

        var errors = DefinitionValidator.Validate(definition);

        Assert.Contains(errors, e => e.Contains("{tag}", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("https", StringComparison.Ordinal));
    }

    [Fact]
    public void A_valid_activation_template_is_accepted()
    {
        var definition = Valid();
        definition.Activation = new NotificationActivation
        {
            UrlTemplate = "https://web.whatsapp.com/send?phone={tag}",
            TagPattern = @"^\d+$",
        };

        Assert.Empty(DefinitionValidator.Validate(definition));
    }

    [Fact]
    public void An_invalid_tag_pattern_is_rejected()
    {
        var definition = Valid();
        definition.Activation = new NotificationActivation
        {
            UrlTemplate = "https://web.whatsapp.com/send?phone={tag}",
            TagPattern = "([",
        };

        Assert.Contains(DefinitionValidator.Validate(definition), e => e.Contains("tagPattern", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_fills_defaults_and_lowercases_choices()
    {
        var definition = new ServiceDefinition
        {
            Id = "  WhatsApp  ",
            Name = " WhatsApp ",
            Url = " https://web.whatsapp.com/ ",
            NotificationMode = " NATIVE ",
            PageDarkMode = " AUTO ",
            MinApiVersion = 0,
            ApiVersion = 0,
        };

        DefinitionValidator.Normalize(definition);

        Assert.Equal("whatsapp", definition.Id);
        Assert.Equal("WhatsApp", definition.Name);
        Assert.Equal(ServiceDefinition.NativeMode, definition.NotificationMode);
        Assert.Equal(ServiceDefinition.PageDarkAuto, definition.PageDarkMode);
        Assert.Equal(1, definition.MinApiVersion);
        Assert.Equal(DefinitionValidator.HostApiVersion, definition.ApiVersion);
    }

    [Fact]
    public void Normalize_falls_back_to_safe_choices()
    {
        var definition = new ServiceDefinition { NotificationMode = "magic", PageDarkMode = "force" };

        DefinitionValidator.Normalize(definition);

        Assert.Equal(ServiceDefinition.NativeMode, definition.NotificationMode);
        Assert.Equal(ServiceDefinition.PageDarkOff, definition.PageDarkMode);
    }
}
