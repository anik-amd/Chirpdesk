using System.Text;
using Chirpdesk.Core.Definitions;

namespace Chirpdesk.Core.Settings;

/// <summary>
/// The only way to add a site in v1: pick an installed definition, name the account, and write
/// a cold instance with a fresh profile. No free-form URL (ADR 0011).
/// </summary>
public static class InstanceFactory
{
    public static ServiceInstance Create(
        ServiceDefinition definition,
        string? displayName,
        IReadOnlyList<ServiceInstance> existing)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(existing);

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            throw new ArgumentException("the definition has no id", nameof(definition));
        }

        if (!Uri.TryCreate(definition.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("the definition has no https url", nameof(definition));
        }

        var name = string.IsNullOrWhiteSpace(displayName) ? definition.Name : displayName.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("the instance has no name", nameof(displayName));
        }

        var id = UniqueId(name, existing);
        var order = existing.Count == 0 ? 0 : existing.Max(instance => instance.Order) + 1;

        return new ServiceInstance
        {
            InstanceId = id,
            DefinitionId = definition.Id,
            Name = name,
            Url = null,
            WarmAtBoot = false,
            ProfileId = id,
            Order = order,
        };
    }

    private static string UniqueId(string name, IReadOnlyList<ServiceInstance> existing)
    {
        var builder = new StringBuilder();
        var lastDash = false;

        foreach (var character in name.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                lastDash = false;
            }
            else if (!lastDash && builder.Length > 0)
            {
                builder.Append('-');
                lastDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');

        if (slug.Length == 0)
        {
            slug = "service";
        }

        if (slug.Length > 32)
        {
            slug = slug[..32].TrimEnd('-');
        }

        var unique = slug;
        var suffix = 2;
        var taken = existing.Select(instance => instance.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (taken.Contains(unique))
        {
            unique = $"{slug}-{suffix++}";
        }

        return unique;
    }
}
