using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Userscripts;

namespace Chirpdesk.Userscripts;

/// <summary>One userscript on disk, with the source it came from and whether the user enabled it.</summary>
internal sealed class InstalledUserscript
{
    public required string Id { get; init; }

    public required Userscript Script { get; init; }

    public required string Path { get; init; }

    public string? SourceUrl { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public bool Enabled { get; init; }
}

/// <summary>A downloaded script waiting for the user's consent. Nothing is written before that.</summary>
internal sealed class UserscriptCandidate
{
    public required Userscript Script { get; init; }

    public required string SourceUrl { get; init; }

    public required string Sha256 { get; init; }

    public required string Raw { get; init; }
}

/// <summary>
/// Third-party userscripts. They are code, so this is deliberately separate from the plugin
/// registry: nothing is signed, nothing installs silently, and every install shows the source,
/// matches and hash before it is written.
/// </summary>
internal sealed class UserscriptService
{
    private const int MaxBytes = 512 * 1024;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ILog _log;
    private readonly string _directory;
    private IReadOnlyList<InstalledUserscript> _cache = [];

    public UserscriptService(ILog log)
    {
        _log = log;
        _directory = AppPaths.UserscriptsDirectory;
        Directory.CreateDirectory(_directory);
        Reload();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<InstalledUserscript> All() => _cache;

    public IReadOnlyList<InstalledUserscript> EnabledFor(string url) =>
        _cache.Where(item => item.Enabled && SafeApplies(item, url)).ToList();

    public void Reload()
    {
        var items = new List<InstalledUserscript>();

        foreach (var file in Directory.EnumerateFiles(_directory, "*.user.js"))
        {
            try
            {
                var raw = File.ReadAllText(file);
                var script = UserscriptParser.Parse(raw);
                var fileName = Path.GetFileName(file);
                var id = fileName.EndsWith(".user.js", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^".user.js".Length]
                    : Path.GetFileNameWithoutExtension(fileName);
                var meta = ReadMeta(id);
                items.Add(new InstalledUserscript
                {
                    Id = id,
                    Script = script,
                    Path = file,
                    SourceUrl = meta?.SourceUrl,
                    Sha256 = meta?.Sha256 ?? Hash(raw),
                    Enabled = meta?.Enabled ?? true,
                });
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                _log.Warn($"userscript skipped: {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        _cache = items.OrderBy(item => item.Script.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static async Task<UserscriptCandidate> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Use an https URL to a .user.js file.");
        }

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The source returned {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaxBytes)
        {
            throw new InvalidOperationException("The script is larger than 512 KB.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new InvalidOperationException("The script is larger than 512 KB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var raw = Encoding.UTF8.GetString(buffer.ToArray());
        var script = UserscriptParser.Parse(raw);
        return new UserscriptCandidate
        {
            Script = script,
            SourceUrl = uri.ToString(),
            Sha256 = Hash(raw),
            Raw = raw,
        };
    }

    /// <summary>Reads a local .user.js for the same consent step as a downloaded one.</summary>
    public static UserscriptCandidate ReadFile(string path)
    {
        var raw = File.ReadAllText(path);
        return new UserscriptCandidate
        {
            Script = UserscriptParser.Parse(raw),
            SourceUrl = path,
            Sha256 = Hash(raw),
            Raw = raw,
        };
    }

    /// <summary>Writes the script and its sidecar. The caller has already shown the consent screen.</summary>
    public InstalledUserscript Install(UserscriptCandidate candidate)
    {
        var id = IdFor(candidate.Script);
        var path = Path.Combine(_directory, id + ".user.js");
        File.WriteAllText(path, candidate.Raw);

        var meta = new Meta
        {
            SourceUrl = candidate.SourceUrl,
            Sha256 = candidate.Sha256,
            Enabled = true,
        };
        File.WriteAllText(Path.Combine(_directory, id + ".json"), JsonSerializer.Serialize(meta, JsonOptions));
        _log.Info($"userscript installed: '{candidate.Script.Name}' ({id})");

        Reload();
        return _cache.First(item => item.Id == id);
    }

    public void SetEnabled(string id, bool enabled)
    {
        var meta = ReadMeta(id) ?? new Meta();
        meta.Enabled = enabled;
        File.WriteAllText(Path.Combine(_directory, id + ".json"), JsonSerializer.Serialize(meta, JsonOptions));
        Reload();
    }

    public void Remove(string id)
    {
        TryDelete(Path.Combine(_directory, id + ".user.js"));
        TryDelete(Path.Combine(_directory, id + ".json"));
        _log.Info($"userscript removed: {id}");
        Reload();
    }

    /// <summary>Scripts are code the user chose; nothing here is ever loaded as a .NET assembly.</summary>
    public static string Wrap(InstalledUserscript installed)
    {
        var script = installed.Script;
        var id = JsonSerializer.Serialize(installed.Id);
        var name = JsonSerializer.Serialize(script.Name);
        var version = JsonSerializer.Serialize(script.Version ?? string.Empty);

        return $$"""
            (function () {
              window.__chirpdeskScripts = window.__chirpdeskScripts || {};
              if (window.__chirpdeskScripts[{{id}}]) { return; }
              window.__chirpdeskScripts[{{id}}] = true;
              var prefix = 'chirpdesk.userscript.' + {{id}} + '.';
              var unsafeWindow = window;
              var GM_info = { script: { name: {{name}}, version: {{version}} } };
              function GM_addStyle(css) {
                var style = document.createElement('style');
                style.textContent = css;
                document.documentElement.appendChild(style);
                return style;
              }
              function GM_setValue(key, value) {
                try { localStorage.setItem(prefix + key, JSON.stringify(value)); } catch (e) { }
              }
              function GM_getValue(key, fallback) {
                try {
                  var raw = localStorage.getItem(prefix + key);
                  return raw === null ? fallback : JSON.parse(raw);
                } catch (e) { return fallback; }
              }
              function GM_deleteValue(key) {
                try { localStorage.removeItem(prefix + key); } catch (e) { }
              }
              function GM_log() {
                try { console.log.apply(console, arguments); } catch (e) { }
              }
              try {
                (function () {
            {{script.Body}}
                })();
              } catch (e) {
                console.warn('Chirpdesk userscript failed: ' + {{name}}, e);
              }
            })();
            """;
    }

    private static bool SafeApplies(InstalledUserscript installed, string url)
    {
        try
        {
            return installed.Script.AppliesTo(url);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private Meta? ReadMeta(string id)
    {
        var path = Path.Combine(_directory, id + ".json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Meta>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

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

    private sealed class Meta
    {
        [JsonPropertyName("sourceUrl")]
        public string? SourceUrl { get; set; }

        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
