using Chirpdesk.Core.Settings;

namespace Chirpdesk.Core.Lifecycle;

/// <summary>
/// Which services warm at boot, in user order. Warm-up is staggered so login does not start
/// several Chromium trees at once; the autostart launch adds a single initial delay.
/// </summary>
public static class BootPlan
{
    public static readonly TimeSpan Stagger = TimeSpan.FromSeconds(1.5);

    public static IReadOnlyList<ServiceInstance> Select(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Services
            .Where(instance => instance.WarmAtBoot)
            .OrderBy(instance => instance.Order)
            .ThenBy(instance => instance.InstanceId, StringComparer.Ordinal)
            .ToList();
    }

    public static TimeSpan DelayFor(int index, TimeSpan initialDelay)
    {
        if (index <= 0)
        {
            return initialDelay < TimeSpan.Zero ? TimeSpan.Zero : initialDelay;
        }

        var delay = initialDelay + (Stagger * index);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
