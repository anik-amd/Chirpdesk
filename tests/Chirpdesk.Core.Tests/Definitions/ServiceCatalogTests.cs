using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Tests.Definitions;

public class ServiceCatalogTests
{
    private static ServiceDefinition WhatsApp() => new()
    {
        Id = "whatsapp",
        Name = "WhatsApp",
        Url = "https://web.whatsapp.com/",
        NotificationMode = ServiceDefinition.NativeMode,
        PageDarkMode = ServiceDefinition.PageDarkOff,
        UnreadRegex = @"\((\d+)\)",
        ApiVersion = DefinitionValidator.HostApiVersion,
    };

    private static ServiceInstance Instance(Action<ServiceInstance>? configure = null)
    {
        var instance = new ServiceInstance
        {
            InstanceId = "whatsapp-work",
            DefinitionId = "whatsapp",
            Name = "WhatsApp Work",
        };

        configure?.Invoke(instance);
        return instance;
    }

    [Fact]
    public void Instance_values_win_over_the_definition()
    {
        var catalog = new ServiceCatalog([WhatsApp()]);

        var resolved = catalog.Resolve(Instance(i =>
        {
            i.Url = "https://web.whatsapp.com/?work";
            i.UnreadRegex = @"\[(\d+)\]";
            i.NotificationMode = ServiceDefinition.ShimMode;
            i.PageDarkMode = ServiceDefinition.PageDarkAuto;
        }))!;

        Assert.Equal("https://web.whatsapp.com/?work", resolved.Url);
        Assert.Equal(@"\[(\d+)\]", resolved.UnreadRegex);
        Assert.Equal(ServiceDefinition.ShimMode, resolved.NotificationMode);
        Assert.Equal(ServiceDefinition.PageDarkAuto, resolved.PageDarkMode);
    }

    [Fact]
    public void A_definition_supplies_what_the_instance_leaves_empty()
    {
        var catalog = new ServiceCatalog([WhatsApp()]);

        var resolved = catalog.Resolve(Instance())!;

        Assert.Equal("https://web.whatsapp.com/", resolved.Url);
        Assert.Equal("WhatsApp Work", resolved.Name);
        Assert.Equal("whatsapp", resolved.DefinitionId);
        Assert.Equal(ServiceDefinition.NativeMode, resolved.NotificationMode);
        Assert.Equal(ServiceDefinition.PageDarkOff, resolved.PageDarkMode);
    }

    [Fact]
    public void Page_dark_null_inherits_the_definition_and_off_overrides_it()
    {
        var definition = WhatsApp();
        definition.PageDarkMode = ServiceDefinition.PageDarkAuto;
        var catalog = new ServiceCatalog([definition]);

        var inherited = catalog.Resolve(Instance(i => i.PageDarkMode = null))!;
        var overridden = catalog.Resolve(Instance(i => i.PageDarkMode = ServiceDefinition.PageDarkOff))!;

        Assert.Equal(ServiceDefinition.PageDarkAuto, inherited.PageDarkMode);
        Assert.Equal(ServiceDefinition.PageDarkOff, overridden.PageDarkMode);
    }

    [Fact]
    public void A_custom_site_resolves_without_a_definition()
    {
        var catalog = new ServiceCatalog([]);

        var resolved = catalog.Resolve(new ServiceInstance
        {
            InstanceId = "my-chat",
            Name = "My Chat",
            Url = "https://chat.example.com/",
        })!;

        Assert.Null(resolved.DefinitionId);
        Assert.Equal(ServiceCatalog.DefaultUnreadRegex, resolved.UnreadRegex);
        Assert.Equal(ServiceDefinition.PageDarkOff, resolved.PageDarkMode);
    }

    [Fact]
    public void An_unknown_definition_falls_back_to_the_instance_url()
    {
        var catalog = new ServiceCatalog([]);

        var resolved = catalog.Resolve(Instance(i => i.Url = "https://web.whatsapp.com/"))!;

        Assert.Null(resolved.DefinitionId);
        Assert.Equal("https://web.whatsapp.com/", resolved.Url);
    }

    [Fact]
    public void An_instance_without_a_url_or_definition_does_not_resolve()
    {
        var catalog = new ServiceCatalog([]);

        Assert.Null(catalog.Resolve(Instance()));
    }
}
