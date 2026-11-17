using Chirpdesk.Core.Lifecycle;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Ordering;

namespace Chirpdesk.Hosting;

/// <summary>
/// Owns the warm services. One runtime per instance id, at most one active at a time; the rest
/// are parked at 1×1 and keep running. The MRU list is the cycle order the Command Bar will
/// use, and activation is the only operation that changes which webview is on screen.
/// </summary>
internal sealed class ServiceRegistry
{
    private readonly ILog _log;
    private readonly Dictionary<string, ServiceRuntime> _warm = new(StringComparer.OrdinalIgnoreCase);
    private readonly MruList _mru = new();
    private readonly LifecycleTracker _state = new();

    public ServiceRegistry(ILog log) => _log = log;

    public ServiceRuntime? Active { get; private set; }

    /// <summary>Raised after <see cref="Active"/> changes, including to null.</summary>
    public event EventHandler? ActiveChanged;

    public RuntimeState StateOf(string instanceId) => _state.StateOf(instanceId);

    /// <summary>Most-recently-used first.</summary>
    public IReadOnlyList<string> Order => _mru.Items;

    public bool IsWarm(string instanceId) => _warm.ContainsKey(instanceId);

    public ServiceRuntime? Find(string instanceId) =>
        _warm.TryGetValue(instanceId, out var runtime) ? runtime : null;

    /// <summary>Replaces a runtime of the same id, disposing the previous one.</summary>
    public async Task AddAsync(ServiceRuntime runtime)
    {
        if (_warm.TryGetValue(runtime.InstanceId, out var existing))
        {
            _warm.Remove(runtime.InstanceId);
            _mru.Remove(runtime.InstanceId);

            if (!ReferenceEquals(existing, Active))
            {
                await existing.DisposeAsync();
            }
        }

        _warm[runtime.InstanceId] = runtime;
        _mru.Activate(runtime.InstanceId);
        _state.MarkWarm(runtime.InstanceId);
    }

    /// <summary>Parks the current service and activates the requested one. False when cold.</summary>
    public bool Activate(string instanceId)
    {
        if (!_warm.TryGetValue(instanceId, out var target))
        {
            return false;
        }

        if (!ReferenceEquals(Active, target))
        {
            Active?.Park();
            target.Activate();
            Active = target;
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        _mru.Activate(instanceId);
        _log.Info($"active service: {instanceId}");
        return true;
    }

    public void ParkActive() => Active?.Park();

    /// <summary>
    /// Closes one warm tab: disposes its runtime and makes the instance cold again. If it was
    /// active, the first remaining MRU tab takes over; with none left, there is no active page.
    /// </summary>
    public async Task<bool> CloseAsync(string instanceId)
    {
        if (!_warm.TryGetValue(instanceId, out var runtime))
        {
            return false;
        }

        _warm.Remove(instanceId);
        _mru.Remove(instanceId);

        var wasActive = ReferenceEquals(Active, runtime);
        if (wasActive)
        {
            Active = null;
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        await runtime.DisposeAsync();
        _state.MarkCold(instanceId);

        if (wasActive && _mru.Items.Count > 0)
        {
            Activate(_mru.Items[0]);
        }

        _log.Info($"closed service: {instanceId}");
        return true;
    }

    /// <summary>
    /// Disposes a runtime but keeps the instance addressable as asleep. The profile and session
    /// stay on disk; the next activation wakes it through the normal create path. Sleeping the
    /// active tab hands over to the first remaining MRU tab, or leaves no active page.
    /// </summary>
    public async Task<bool> SleepAsync(string instanceId)
    {
        if (!_warm.TryGetValue(instanceId, out var runtime))
        {
            return false;
        }

        _warm.Remove(instanceId);
        _mru.Remove(instanceId);

        var wasActive = ReferenceEquals(Active, runtime);
        if (wasActive)
        {
            Active = null;
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        await runtime.DisposeAsync();
        _state.MarkAsleep(instanceId);

        if (wasActive && _mru.Items.Count > 0)
        {
            Activate(_mru.Items[0]);
        }

        _log.Info($"slept service: {instanceId}");
        return true;
    }

    /// <summary>Next/previous warm service in MRU order. False when there is nothing to move to.</summary>
    public bool Cycle(int direction)
    {
        var next = _mru.Cycle(Active?.InstanceId, direction);
        return next is not null && Activate(next);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var runtime in _warm.Values)
        {
            await runtime.DisposeAsync();
        }

        _warm.Clear();
        Active = null;
    }
}
