using System.IO;
using System.Security.Cryptography;
using Chirpdesk.Core.Integrations;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Notifications;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Integrations;

internal sealed record IntegrationRuntimeStatus(
    IntegrationState State,
    string? Error,
    int Unread,
    string? HelperStatus);

/// <summary>
/// Owns every installed integration session for the running app. The host reads the source
/// sidecar, refuses a changed executable, starts enabled helpers, routes notifications to the
/// gated toast sink and exposes status to the Plugins page.
/// </summary>
internal sealed class IntegrationRuntimeHost
{
    private readonly ILog _log;
    private readonly IToastSink _toasts;
    private readonly Action<Action> _dispatch;
    private readonly string _appVersion;
    private readonly SettingsStore _store;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public IntegrationRuntimeHost(
        ILog log,
        IToastSink toasts,
        Action<Action> dispatch,
        string appVersion,
        SettingsStore store)
    {
        _log = log;
        _toasts = toasts;
        _dispatch = dispatch;
        _appVersion = appVersion;
        _store = store;
    }

    /// <summary>Raised off the UI thread when the total contribution changes.</summary>
    public event Action? UnreadChanged;

    /// <summary>Raised off the UI thread with the plugin id whose state changed.</summary>
    public event Action<string>? StatusChanged;

    public int UnreadTotal
    {
        get
        {
            lock (_lock)
            {
                return _entries.Values.Sum(entry => entry.Unread);
            }
        }
    }

    public IntegrationRuntimeStatus? StatusOf(string pluginId)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(pluginId, out var entry)
                ? new IntegrationRuntimeStatus(entry.Session.State, entry.Session.LastError, entry.Unread, entry.HelperStatus)
                : null;
        }
    }

    public (string Title, string Argument)? DescribeCommand(string pluginId, string commandId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(pluginId, out var entry))
            {
                return null;
            }

            var command = entry.Manifest.Commands.FirstOrDefault(item =>
                string.Equals(item.Id, commandId, StringComparison.Ordinal));

            return command is null ? null : (command.Title, command.Argument);
        }
    }

    public IReadOnlyList<(string PluginId, string PluginName, IntegrationCommand Command)> Commands()
    {
        lock (_lock)
        {
            return _entries.Values
                .SelectMany(entry => entry.Manifest.Commands.Select(command =>
                    (entry.PluginId, entry.PluginName, command)))
                .ToList();
        }
    }

    /// <summary>
    /// Reconciles the running sessions with the installed catalogue. Removed, disabled or updated
    /// plugins are stopped; new ones start. Safe to call after any catalogue reload.
    /// </summary>
    public async Task SyncAsync(IReadOnlyList<InstalledPlugin> inventory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var settings = _store.Load();
        var desired = inventory
            .Where(plugin => string.Equals(plugin.Kind, PluginLoader.IntegrationKind, StringComparison.Ordinal))
            .Where(plugin => PluginState.IsEnabled(settings, plugin.Id))
            .Where(plugin => !string.IsNullOrWhiteSpace(plugin.Version))
            .ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);

        List<string> removed;

        lock (_lock)
        {
            removed = _entries.Keys.Where(id => !desired.ContainsKey(id)).ToList();
        }

        foreach (var id in removed)
        {
            await RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        }

        foreach (var plugin in desired.Values)
        {
            Entry? existing;

            lock (_lock)
            {
                _entries.TryGetValue(plugin.Id, out existing);
            }

            if (existing is not null)
            {
                if (string.Equals(existing.Version, plugin.Version, StringComparison.Ordinal))
                {
                    continue;
                }

                await RemoveAsync(plugin.Id, cancellationToken).ConfigureAwait(false);
            }

            var entry = CreateEntry(plugin);

            if (entry is null)
            {
                continue;
            }

            lock (_lock)
            {
                _entries[plugin.Id] = entry;
            }

            StartEntry(entry);
        }
    }

    public async Task StartAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        Entry? entry;

        lock (_lock)
        {
            _entries.TryGetValue(pluginId, out entry);
        }

        if (entry is null)
        {
            return;
        }

        if (!VerifyExecutable(entry))
        {
            entry.Session.Fail("helper changed on disk");
            RaiseStatus(pluginId);
            return;
        }

        await entry.Session.StartAsync(cancellationToken).ConfigureAwait(false);
        RaiseStatus(pluginId);
    }

    /// <summary>Stops the helper but keeps the entry so Start can bring it back.</summary>
    public Task StopAsync(string pluginId, CancellationToken cancellationToken = default) =>
        StopCoreAsync(pluginId, removeEntry: false, cancellationToken);

    /// <summary>Stops and forgets the helper; used before an update, uninstall or catalogue change.</summary>
    public Task RemoveAsync(string pluginId, CancellationToken cancellationToken = default) =>
        StopCoreAsync(pluginId, removeEntry: true, cancellationToken);

    private async Task StopCoreAsync(string pluginId, bool removeEntry, CancellationToken cancellationToken)
    {
        Entry? entry;

        lock (_lock)
        {
            if (!_entries.TryGetValue(pluginId, out entry))
            {
                return;
            }

            if (removeEntry)
            {
                _entries.Remove(pluginId);
            }
        }

        await entry.Session.StopAsync(cancellationToken).ConfigureAwait(false);

        if (removeEntry)
        {
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }

        entry.Unread = 0;
        RaiseUnread();
        RaiseStatus(pluginId);
    }

    public async Task<IntegrationCommandResult> SendCommandAsync(
        string pluginId,
        string commandId,
        string? argument,
        CancellationToken cancellationToken = default)
    {
        Entry? entry;

        lock (_lock)
        {
            _entries.TryGetValue(pluginId, out entry);
        }

        if (entry is null)
        {
            return IntegrationCommandResult.Failure("integration is not installed or is disabled");
        }

        if (entry.Session.State != IntegrationState.Running && !VerifyExecutable(entry))
        {
            entry.Session.Fail("helper changed on disk");
            RaiseStatus(pluginId);
            return IntegrationCommandResult.Failure("helper changed on disk");
        }

        if (entry.Session.State != IntegrationState.Running)
        {
            await entry.Session.StartAsync(cancellationToken).ConfigureAwait(false);

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

            while (entry.Session.State == IntegrationState.Starting && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        return await entry.Session.SendCommandAsync(commandId, argument, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        List<string> ids;

        lock (_lock)
        {
            ids = _entries.Keys.ToList();
        }

        foreach (var id in ids)
        {
            try
            {
                await RemoveAsync(id).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _log.Warn($"integration '{id}' did not stop cleanly: {ex.Message}");
            }
        }
    }

    private Entry? CreateEntry(InstalledPlugin plugin)
    {
        var folder = Path.Combine(
            plugin.Bundled ? AppPaths.BundledPluginsDirectory : AppPaths.UserPluginsDirectory,
            plugin.Id);

        var manifest = IntegrationLoader.Read(folder, out var errors);

        if (manifest is null)
        {
            _log.Warn($"integration '{plugin.Id}' skipped: {string.Join("; ", errors)}");
            return null;
        }

        var pluginManifest = PluginLoader.ReadInstalledManifest(folder, out var pluginErrors);

        if (pluginManifest is null)
        {
            _log.Warn($"integration '{plugin.Id}' skipped: {string.Join("; ", pluginErrors)}");
            return null;
        }

        var executable = Path.Combine(folder, manifest.Executable);
        var source = PluginSourceStore.TryRead(folder);

        if (string.IsNullOrWhiteSpace(source?.ExecutableSha256))
        {
            _log.Warn($"integration '{plugin.Id}' has no pinned executable hash; refusing to launch");
            return null;
        }

        string actualHash;

        try
        {
            using var stream = File.OpenRead(executable);
            actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"integration '{plugin.Id}' executable is unreadable: {ex.Message}");
            return null;
        }

        if (!string.Equals(actualHash, source.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warn($"integration '{plugin.Id}' executable changed on disk; refusing to launch");
            return null;
        }

        var permissions = new HashSet<string>(pluginManifest.Permissions ?? [], StringComparer.Ordinal);
        var entry = new Entry(plugin.Id, plugin.Name, plugin.Version ?? string.Empty, manifest, executable, source.ExecutableSha256);

        entry.Session = new IntegrationSession(
            manifest,
            plugin.Id,
            executable,
            Path.Combine(AppPaths.DataRoot, "integrations", plugin.Id),
            permissions,
            new WindowsIntegrationLauncher(_log),
            new SystemIntegrationDelay(),
            _log,
            new IntegrationSessionOptions { AppVersion = _appVersion });

        entry.Session.NotificationReceived += message => OnNotification(entry, message);
        entry.Session.UnreadChanged += count => OnUnread(entry, count);
        entry.Session.HelperStatus += status => OnHelperStatus(entry, status);
        entry.Session.StateChanged += (_, changed) => OnState(entry, changed);

        return entry;
    }

    private bool VerifyExecutable(Entry entry)
    {
        try
        {
            using var stream = File.OpenRead(entry.ExecutablePath);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

            if (!string.Equals(actual, entry.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn($"integration '{entry.PluginId}' executable changed on disk; refusing to launch");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"integration '{entry.PluginId}' executable is unreadable: {ex.Message}");
            return false;
        }
    }

    private void StartEntry(Entry entry) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await entry.Session.StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _log.Warn($"integration '{entry.PluginId}' failed to start: {ex.Message}");
            }
        });

    private void OnNotification(Entry entry, IntegrationNotifyMessage message)
    {
        var body = message.Body ?? string.Empty;
        _dispatch(() => _toasts.Show(new ToastMessage(
            message.Title,
            body,
            null,
            $"integration:{entry.PluginId}")));
    }

    private void OnUnread(Entry entry, int count)
    {
        entry.Unread = Math.Clamp(count, 0, IntegrationProtocol.MaxUnreadCount);
        RaiseUnread();
    }

    private void OnHelperStatus(Entry entry, IntegrationStatusMessage status)
    {
        entry.HelperStatus = string.IsNullOrWhiteSpace(status.Detail)
            ? status.State
            : $"{status.State}: {status.Detail}";
        RaiseStatus(entry.PluginId);
    }

    private void OnState(Entry entry, IntegrationStateChanged changed)
    {
        if (changed.State is IntegrationState.Stopped or IntegrationState.Error)
        {
            entry.Unread = 0;
            RaiseUnread();
        }

        RaiseStatus(entry.PluginId);
    }

    private void RaiseUnread() => UnreadChanged?.Invoke();

    private void RaiseStatus(string pluginId) => StatusChanged?.Invoke(pluginId);

    private sealed class Entry(
        string pluginId,
        string pluginName,
        string version,
        IntegrationManifest manifest,
        string executablePath,
        string expectedSha256)
    {
        public string PluginId { get; } = pluginId;

        public string PluginName { get; } = pluginName;

        public string Version { get; } = version;

        public IntegrationManifest Manifest { get; } = manifest;

        public string ExecutablePath { get; } = executablePath;

        public string ExpectedSha256 { get; } = expectedSha256;

        public IntegrationSession Session { get; set; } = null!;

        public int Unread { get; set; }

        public string? HelperStatus { get; set; }
    }
}
