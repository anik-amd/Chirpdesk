using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Definitions;

/// <summary>
/// The runtime values for one instance after a definition is applied. This is what the host
/// consumes: it never reads a raw definition or a raw instance again.
/// </summary>
public sealed record ResolvedService(
    string InstanceId,
    string Name,
    string Url,
    string UnreadRegex,
    string NotificationMode,
    string PageDarkMode,
    string? DefinitionId);

/// <summary>
/// Joins definitions and instances. Instance wins over definition; a definition supplies what
/// the instance leaves empty. A custom site (no definition id) resolves from its own row. An
/// unknown definition id with a URL still resolves as a custom site, because losing the user's
/// service would be the worse failure.
/// </summary>
public sealed class ServiceCatalog
{
    public const string DefaultUnreadRegex = @"\((\d+)\)";

    private readonly Dictionary<string, ServiceDefinition> _definitions;

    public ServiceCatalog(IEnumerable<ServiceDefinition> definitions)
    {
        _definitions = definitions
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ServiceDefinition> Definitions => _definitions.Values;

    public ServiceDefinition? Find(string? definitionId) =>
        definitionId is not null && _definitions.TryGetValue(definitionId, out var definition)
            ? definition
            : null;

    public ResolvedService? Resolve(ServiceInstance instance)
    {
        var definition = Find(instance.DefinitionId);

        var url = !string.IsNullOrWhiteSpace(instance.Url)
            ? instance.Url
            : definition?.Url;

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var name = !string.IsNullOrWhiteSpace(instance.Name)
            ? instance.Name
            : definition?.Name ?? instance.InstanceId;

        var unreadRegex = !string.IsNullOrWhiteSpace(instance.UnreadRegex)
            ? instance.UnreadRegex
            : (definition?.UnreadRegex ?? DefaultUnreadRegex);

        var notificationMode = !string.IsNullOrWhiteSpace(instance.NotificationMode)
            ? instance.NotificationMode
            : (definition?.NotificationMode ?? ServiceDefinition.NativeMode);

        var pageDarkMode = !string.IsNullOrWhiteSpace(instance.PageDarkMode)
            ? instance.PageDarkMode
            : (definition?.PageDarkMode ?? ServiceDefinition.PageDarkOff);

        return new ResolvedService(
            instance.InstanceId,
            name,
            url,
            unreadRegex,
            notificationMode,
            pageDarkMode,
            definition?.Id);
    }
}
