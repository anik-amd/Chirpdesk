namespace Chirpdesk.Core.Lifecycle;

/// <summary>
/// What a service is doing this session. Separate from <c>warmAtBoot</c>, which is the persisted
/// pin: a pinned service can be asleep until the next launch, and a cold one has no webview at all.
/// </summary>
public enum RuntimeState
{
    Cold,
    Asleep,
    Warm,
}

/// <summary>Per-instance runtime state, with no WPF or webview in it.</summary>
public sealed class LifecycleTracker
{
    private readonly Dictionary<string, RuntimeState> _states = new(StringComparer.OrdinalIgnoreCase);

    public RuntimeState StateOf(string instanceId) =>
        _states.GetValueOrDefault(instanceId, RuntimeState.Cold);

    public void MarkWarm(string instanceId) => _states[instanceId] = RuntimeState.Warm;

    public void MarkAsleep(string instanceId) => _states[instanceId] = RuntimeState.Asleep;

    public void MarkCold(string instanceId) => _states[instanceId] = RuntimeState.Cold;

    public void Forget(string instanceId) => _states.Remove(instanceId);
}
