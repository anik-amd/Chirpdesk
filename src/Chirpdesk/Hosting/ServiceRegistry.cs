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

    public ServiceRegistry(ILog log) => _log = log;

    public ServiceRuntime? Active { get; private set; }

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
        }

        _mru.Activate(instanceId);
        _log.Info($"active service: {instanceId}");
        return true;
    }

    public void ParkActive() => Active?.Park();

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
