using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Tests.Settings;

public class InstanceFactoryTests
{
    private static ServiceDefinition WhatsApp() => new()
    {
        Id = "whatsapp",
        Name = "WhatsApp",
        Url = "https://web.whatsapp.com/",
        ApiVersion = DefinitionValidator.HostApiVersion,
    };

    [Fact]
    public void A_new_instance_uses_the_definition_and_starts_cold()
    {
        var instance = InstanceFactory.Create(WhatsApp(), displayName: null, []);

        Assert.Equal("whatsapp", instance.DefinitionId);
        Assert.Equal("WhatsApp", instance.Name);
        Assert.Null(instance.Url);
        Assert.True(instance.WarmAtBoot);
        Assert.Equal(instance.InstanceId, instance.ProfileId);
    }

    [Fact]
    public void Two_accounts_get_distinct_ids_profiles_and_order()
    {
        var first = InstanceFactory.Create(WhatsApp(), "WhatsApp Work", []);
        var second = InstanceFactory.Create(WhatsApp(), "WhatsApp Work", [first]);

        Assert.Equal("whatsapp-work", first.InstanceId);
        Assert.Equal("whatsapp-work-2", second.InstanceId);
        Assert.NotEqual(first.ProfileId, second.ProfileId);
        Assert.Equal(0, first.Order);
        Assert.Equal(1, second.Order);
    }

    [Fact]
    public void Path_like_display_names_are_slugged()
    {
        var instance = InstanceFactory.Create(WhatsApp(), "  ../My Chat \\ 2026  ", []);

        Assert.Equal("my-chat-2026", instance.InstanceId);
        Assert.DoesNotContain('.', instance.InstanceId);
        Assert.DoesNotContain('\\', instance.InstanceId);
    }

    [Fact]
    public void A_definition_without_an_https_url_is_refused()
    {
        var definition = WhatsApp();
        definition.Url = "http://insecure.example/";

        Assert.Throws<ArgumentException>(() => InstanceFactory.Create(definition, null, []));
    }

    [Fact]
    public void Ids_and_names_are_never_invented_from_a_missing_definition()
    {
        var definition = WhatsApp();
        definition.Id = string.Empty;

        Assert.Throws<ArgumentException>(() => InstanceFactory.Create(definition, null, []));
    }
}
