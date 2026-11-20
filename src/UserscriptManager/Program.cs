using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chirpdesk.Core.Integrations;
using Chirpdesk.Core.Userscripts;

// The userscript manager runs as an out-of-process integration helper. It owns the script
// folder, the update checks and the per-script toggles; the host only injects what it returns
// and renders the UI. No state is shared in-process.

var pipeName = Environment.GetEnvironmentVariable("CHIRPDESK_PIPE");
var token = Environment.GetEnvironmentVariable("CHIRPDESK_TOKEN");
var protocolText = Environment.GetEnvironmentVariable("CHIRPDESK_PROTOCOL");

if (string.IsNullOrWhiteSpace(pipeName)
    || string.IsNullOrWhiteSpace(token)
    || !int.TryParse(protocolText, out var protocolVersion))
{
    return 2;
}

var dataDirectory = Environment.GetEnvironmentVariable("CHIRPDESK_DATA");
if (string.IsNullOrWhiteSpace(dataDirectory))
{
    return 3;
}

using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    await pipe.ConnectAsync(5000);
}
catch (TimeoutException)
{
    return 4;
}

await IntegrationFraming.WriteAsync(
    pipe,
    JsonRpcCodec.Request(1, "initialize", new
    {
        protocolVersion,
        token,
        pluginVersion = "1.0.0",
        appVersion = "chirpdesk",
    }));

var initialize = await ReadEnvelopeAsync(pipe);
if (initialize is null || initialize.Kind != JsonRpcKind.Response || initialize.Error is not null)
{
    return 5;
}

var manager = new Manager(dataDirectory);
await manager.LoadAsync();

_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
    while (await timer.WaitForNextTickAsync())
    {
        try
        {
            await manager.MaybeCheckAsync();
        }
        catch
        {
            // A failed background check is retried on the next tick; the UI shows the last error.
        }
    }
});

while (true)
{
    JsonRpcEnvelope? envelope;
    try
    {
        envelope = await ReadEnvelopeAsync(pipe);
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
    {
        break;
    }

    if (envelope is null)
    {
        break;
    }

    if (envelope.Kind == JsonRpcKind.Notification)
    {
        if (string.Equals(envelope.Method, "shutdown", StringComparison.Ordinal))
        {
            break;
        }

        continue;
    }

    if (envelope.Kind != JsonRpcKind.Request)
    {
        continue;
    }

    if (string.Equals(envelope.Method, "ping", StringComparison.Ordinal))
    {
        await IntegrationFraming.WriteAsync(pipe, JsonRpcCodec.Response(envelope.Id, new { ok = true }));
        continue;
    }

    if (!string.Equals(envelope.Method, "command", StringComparison.Ordinal))
    {
        await IntegrationFraming.WriteAsync(pipe, JsonRpcCodec.ErrorResponse(envelope.Id, -32601, "method not found"));
        continue;
    }

    var id = envelope.Params.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
        ? idElement.GetString() ?? string.Empty
        : string.Empty;
    var argument = envelope.Params.TryGetProperty("argument", out var argumentElement)
        && argumentElement.ValueKind == JsonValueKind.String
        ? argumentElement.GetString()
        : null;

    try
    {
        var message = await manager.RunAsync(id, argument, CancellationToken.None);
        await IntegrationFraming.WriteAsync(
            pipe,
            JsonRpcCodec.Response(envelope.Id, new { ok = true, message }));
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or HttpRequestException
        or TaskCanceledException or IOException or FormatException)
    {
        await IntegrationFraming.WriteAsync(
            pipe,
            JsonRpcCodec.Response(envelope.Id, new { ok = false, error = ex.Message }));
    }

}

return 0;

static async Task<JsonRpcEnvelope?> ReadEnvelopeAsync(Stream stream)
{
    var frame = await IntegrationFraming.ReadAsync(stream);
    if (frame is null)
    {
        return null;
    }

    return JsonRpcCodec.TryParse(frame, out var envelope, out var error)
        ? envelope
        : throw new InvalidOperationException(error);
}

internal sealed class Manager : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _data;
    private readonly string _scripts;
    private readonly string _stateFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Update> _updates = new(StringComparer.Ordinal);

    private Config _config = new();
    private List<Entry> _entries = [];
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public Manager(string data)
    {
        _data = data;
        _scripts = Path.Combine(data, "scripts");
        _stateFile = Path.Combine(data, "state.json");
        Directory.CreateDirectory(_scripts);
    }

    public async Task LoadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (File.Exists(_stateFile))
            {
                try
                {
                    var state = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_stateFile), Json);
                    _config = state?.Config ?? new Config();
                    _entries = state?.Scripts ?? [];
                }
                catch (JsonException)
                {
                    _config = new Config();
                    _entries = [];
                }
            }

            foreach (var entry in _entries)
            {
                var file = ScriptPath(entry.Id);
                if (!File.Exists(file))
                {
                    entry.Enabled = false;
                    continue;
                }

                try
                {
                    entry.Script = UserscriptParser.Parse(File.ReadAllText(file));
                }
                catch (FormatException)
                {
                    entry.Enabled = false;
                }
            }

            _entries = _entries.Where(entry => entry.Script is not null).ToList();
            _lastCheck = DateTimeOffset.TryParse(_config.LastCheck, out var parsed) ? parsed : DateTimeOffset.MinValue;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> RunAsync(string id, string? argument, CancellationToken cancellationToken)
    {
        return id switch
        {
            "userscripts.state" => await StateAsync(cancellationToken),
            "userscripts.add" => await AddAsync(argument, cancellationToken),
            "userscripts.install" => await InstallAsync(argument),
            "userscripts.remove" => await RemoveAsync(argument),
            "userscripts.enable" => await SetEnabledAsync(argument),
            "userscripts.auto" => await SetAutoAsync(argument),
            "userscripts.config" => await SetConfigAsync(argument),
            "userscripts.check" => await CheckAsync(applyAuto: _config.Auto, force: true, cancellationToken),
            "userscripts.update" => await UpdateAsync(argument, cancellationToken),
            "userscripts.scripts" => await ScriptsAsync(argument),
            _ => throw new InvalidOperationException($"unknown command '{id}'"),
        };
    }

    public void Dispose() => _gate.Dispose();

    public async Task MaybeCheckAsync()
    {
        if (!_config.Check)
        {
            return;
        }

        var due = DateTimeOffset.UtcNow - _lastCheck > TimeSpan.FromHours(6);
        if (!due)
        {
            return;
        }

        await CheckAsync(_config.Auto, force: true, CancellationToken.None);
    }

    private async Task<string> StateAsync(CancellationToken cancellationToken)
    {
        var due = _config.Check && DateTimeOffset.UtcNow - _lastCheck > TimeSpan.FromHours(6);
        if (due)
        {
            try
            {
                return await CheckAsync(_config.Auto, force: false, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // The state still renders; the check is retried on the next call or timer.
            }
        }

        return await SnapshotAsync();
    }

    private async Task<string> AddAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Use an https URL to a .user.js file.");
        }

        var raw = await DownloadAsync(uri, cancellationToken);
        var script = UserscriptParser.Parse(raw);

        if (script.Matches.Count == 0 && script.Includes.Count == 0)
        {
            throw new InvalidOperationException("This script has no @match or @include, so it would never run.");
        }

        var token = Guid.NewGuid().ToString("N");
        _pending[token] = new Pending(script, raw, uri.ToString(), DateTimeOffset.UtcNow.AddMinutes(15));
        PrunePending();

        return JsonSerializer.Serialize(new
        {
            token,
            name = script.Name,
            version = script.Version,
            matches = script.Matches,
            includes = script.Includes,
            grants = script.Grants,
            sourceUrl = uri.ToString(),
            sha256 = Hash(raw),
        }, Json);
    }

    private async Task<string> InstallAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !_pending.Remove(token, out var pending))
        {
            throw new InvalidOperationException("The install request expired. Add the script again.");
        }

        await _gate.WaitAsync();
        try
        {
            var id = IdFor(pending.Script);
            await File.WriteAllTextAsync(ScriptPath(id), pending.Raw);

            var entry = _entries.FirstOrDefault(item => item.Id == id);
            if (entry is null)
            {
                entry = new Entry { Id = id };
                _entries.Add(entry);
            }

            entry.Script = pending.Script;
            entry.SourceUrl = pending.SourceUrl;
            entry.UpdateUrl = pending.Script.UpdateUrl ?? pending.SourceUrl;
            entry.Sha256 = Hash(pending.Raw);
            entry.Enabled = true;
            entry.LastChecked = null;
            entry.UpdateAvailable = false;
            _updates.Remove(id);

            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await SnapshotAsync();
    }

    private async Task<string> RemoveAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("No script id.");
        }

        await _gate.WaitAsync();
        try
        {
            _entries.RemoveAll(entry => entry.Id == id);
            _updates.Remove(id);
            TryDelete(ScriptPath(id));
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await SnapshotAsync();
    }

    private async Task<string> SetEnabledAsync(string? argument)
    {
        var request = ParseToggle(argument);
        await _gate.WaitAsync();
        try
        {
            var entry = Find(request.Id);
            entry.Enabled = request.Enabled;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await SnapshotAsync();
    }

    private async Task<string> SetAutoAsync(string? argument)
    {
        var request = ParseToggle(argument);
        await _gate.WaitAsync();
        try
        {
            var entry = Find(request.Id);
            entry.AutoUpdate = request.Enabled;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await SnapshotAsync();
    }

    private async Task<string> SetConfigAsync(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new InvalidOperationException("No config.");
        }

        var request = JsonSerializer.Deserialize<ConfigRequest>(argument, Json)
            ?? throw new InvalidOperationException("No config.");

        await _gate.WaitAsync();
        try
        {
            if (request.Check is { } check)
            {
                _config.Check = check;
            }

            if (request.Auto is { } auto)
            {
                _config.Auto = auto;
            }

            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await SnapshotAsync();
    }

    private async Task<string> CheckAsync(bool applyAuto, bool force, CancellationToken cancellationToken)
    {
        List<Entry> snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            snapshot = _entries.ToList();
        }
        finally
        {
            _gate.Release();
        }

        foreach (var entry in snapshot)
        {
            var url = entry.UpdateUrl ?? entry.SourceUrl;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            try
            {
                var raw = await DownloadAsync(uri, cancellationToken);
                var latest = UserscriptParser.Parse(raw);
                var changed = !string.Equals(latest.Version ?? string.Empty, entry.Version ?? string.Empty, StringComparison.Ordinal);
                var auto = applyAuto || entry.AutoUpdate;

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    entry.LastChecked = DateTimeOffset.UtcNow.ToString("o");

                    if (changed)
                    {
                        if (auto)
                        {
                            await File.WriteAllTextAsync(ScriptPath(entry.Id), raw, cancellationToken);
                            entry.Script = latest;
                            entry.Sha256 = Hash(raw);
                            entry.UpdateAvailable = false;
                            _updates.Remove(entry.Id);
                        }
                        else
                        {
                            entry.UpdateAvailable = true;
                            _updates[entry.Id] = new Update(raw, latest);
                        }
                    }
                    else
                    {
                        entry.UpdateAvailable = false;
                        _updates.Remove(entry.Id);
                    }

                    await SaveAsync();
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                or FormatException)
            {
                await _gate.WaitAsync(cancellationToken);
                try
                {
                    entry.LastChecked = DateTimeOffset.UtcNow.ToString("o");
                    await SaveAsync();
                }
                finally
                {
                    _gate.Release();
                }
            }
        }

        _lastCheck = DateTimeOffset.UtcNow;
        _config.LastCheck = _lastCheck.ToString("o");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await SnapshotAsync();
    }

    private async Task<string> UpdateAsync(string? argument, CancellationToken cancellationToken)
    {
        var ids = string.IsNullOrWhiteSpace(argument) || argument == "all"
            ? _entries.Select(entry => entry.Id).ToList()
            : [argument];

        foreach (var id in ids)
        {
            var entry = _entries.FirstOrDefault(item => item.Id == id);
            if (entry is null)
            {
                continue;
            }

            if (!_updates.TryGetValue(id, out var update))
            {
                var url = entry.UpdateUrl ?? entry.SourceUrl;
                if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                var raw = await DownloadAsync(uri, cancellationToken);
                update = new Update(raw, UserscriptParser.Parse(raw));
            }

            await _gate.WaitAsync(cancellationToken);
            try
            {
                await File.WriteAllTextAsync(ScriptPath(id), update.Raw, cancellationToken);
                entry.Script = update.Script;
                entry.Sha256 = Hash(update.Raw);
                entry.UpdateAvailable = false;
                entry.LastChecked = DateTimeOffset.UtcNow.ToString("o");
                _updates.Remove(id);
                await SaveAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        return await SnapshotAsync();
    }

    private async Task<string> ScriptsAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "[]";
        }

        List<Entry> enabled;
        await _gate.WaitAsync();
        try
        {
            enabled = _entries.Where(entry => entry.Enabled && entry.Script is not null).ToList();
        }
        finally
        {
            _gate.Release();
        }

        var result = new List<object>();
        var budget = 200 * 1024;

        foreach (var entry in enabled)
        {
            var script = entry.Script!;
            bool applies;
            try
            {
                applies = script.AppliesTo(url);
            }
            catch (ArgumentException)
            {
                applies = false;
            }

            if (!applies)
            {
                continue;
            }

            var code = await File.ReadAllTextAsync(ScriptPath(entry.Id));
            if (code.Length > budget)
            {
                continue;
            }

            budget -= code.Length;
            result.Add(new
            {
                id = entry.Id,
                name = script.Name,
                version = script.Version ?? string.Empty,
                code,
            });
        }

        return JsonSerializer.Serialize(result, Json);
    }

    private async Task<string> SnapshotAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var scripts = _entries
                .Where(entry => entry.Script is not null)
                .OrderBy(entry => entry.Script!.Name, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new
                {
                    id = entry.Id,
                    name = entry.Script!.Name,
                    version = entry.Script.Version,
                    enabled = entry.Enabled,
                    autoUpdate = entry.AutoUpdate,
                    matches = entry.Script.Matches.Concat(entry.Script.Includes).ToList(),
                    sourceUrl = entry.SourceUrl,
                    updateAvailable = entry.UpdateAvailable,
                    lastChecked = entry.LastChecked,
                })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                config = new { check = _config.Check, auto = _config.Auto, lastCheck = _config.LastCheck },
                scripts,
            }, Json);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync()
    {
        var state = new Persisted
        {
            Config = _config,
            Scripts = _entries
                .Select(entry => new Entry
                {
                    Id = entry.Id,
                    SourceUrl = entry.SourceUrl,
                    UpdateUrl = entry.UpdateUrl,
                    Sha256 = entry.Sha256,
                    Enabled = entry.Enabled,
                    AutoUpdate = entry.AutoUpdate,
                    UpdateAvailable = entry.UpdateAvailable,
                    LastChecked = entry.LastChecked,
                })
                .ToList(),
        };

        var temp = _stateFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, _stateFile, overwrite: true);
    }

    private Entry Find(string id) =>
        _entries.FirstOrDefault(entry => entry.Id == id)
        ?? throw new InvalidOperationException("No such script.");

    private static ToggleRequest ParseToggle(string? argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new InvalidOperationException("No toggle payload.");
        }

        return JsonSerializer.Deserialize<ToggleRequest>(argument, Json)
            ?? throw new InvalidOperationException("No toggle payload.");
    }

    private static async Task<string> DownloadAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The source returned {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > 512 * 1024)
        {
            throw new InvalidOperationException("The script is larger than 512 KB.");
        }

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (raw.Length > 512 * 1024)
        {
            throw new InvalidOperationException("The script is larger than 512 KB.");
        }

        return raw;
    }

    private void PrunePending()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _pending.Where(pair => pair.Value.Expires < now).Select(pair => pair.Key).ToList())
        {
            _pending.Remove(key);
        }
    }

    private string ScriptPath(string id) => Path.Combine(_scripts, id + ".user.js");

    private static string IdFor(Userscript script)
    {
        var key = (script.Namespace ?? string.Empty) + "\n" + script.Name;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
    }

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class Persisted
    {
        public Config Config { get; set; } = new();

        public List<Entry> Scripts { get; set; } = [];
    }

    private sealed class Config
    {
        public bool Check { get; set; } = true;

        public bool Auto { get; set; }

        public string? LastCheck { get; set; }
    }

    private sealed class Entry
    {
        public string Id { get; set; } = string.Empty;

        [JsonIgnore]
        public Userscript? Script { get; set; }

        public string? SourceUrl { get; set; }

        public string? UpdateUrl { get; set; }

        public string? Sha256 { get; set; }

        public bool Enabled { get; set; } = true;

        public bool AutoUpdate { get; set; }

        public bool UpdateAvailable { get; set; }

        public string? LastChecked { get; set; }

        [JsonIgnore]
        public string? Version => Script?.Version;
    }

    private sealed record Pending(Userscript Script, string Raw, string SourceUrl, DateTimeOffset Expires);

    private sealed record Update(string Raw, Userscript Script);

    private sealed class ToggleRequest
    {
        public string Id { get; set; } = string.Empty;

        public bool Enabled { get; set; }
    }

    private sealed class ConfigRequest
    {
        public bool? Check { get; set; }

        public bool? Auto { get; set; }
    }
}
