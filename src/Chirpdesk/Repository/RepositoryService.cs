using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Plugins;
using Chirpdesk.Core.Settings;

namespace Chirpdesk.Repository;

internal sealed record RepositorySyncResult(
    bool Ok,
    string Message,
    RepositoryIndex? Index = null,
    string? PublicKeyPem = null,
    string? Fingerprint = null,
    bool KeyChanged = false);

internal sealed record RepositoryInstallResult(bool Ok, string Message, string? PluginId = null);

/// <summary>
/// HTTP and filesystem side of a static plugin repository. Every install is gated by the signed
/// index: key fingerprint, exact archive hash/size, archive policy, then a manifest that matches
/// the entry. A failure leaves the existing installed copy untouched.
/// </summary>
internal sealed class RepositoryService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(4);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly SettingsStore _store;
    private readonly ILog _log;

    public RepositoryService(SettingsStore store, ILog log)
    {
        _store = store;
        _log = log;
    }

    public async Task<RepositorySyncResult> SyncAsync(
        RepositorySetting repository,
        CancellationToken cancellationToken = default)
    {
        if (!TryBaseUri(repository.Url, out var baseUri, out var urlError))
        {
            return new RepositorySyncResult(false, urlError);
        }

        try
        {
            var cache = CachePaths(repository.Url);

            if (repository.LastSync is { } lastSync
                && DateTimeOffset.UtcNow - lastSync < CacheTtl
                && TryReadCache(cache, repository, out var cached))
            {
                _log.Info($"repository cache hit: {cached.Index!.Name}");
                return cached;
            }

            var indexUri = new Uri(baseUri, "index.json");
            var signatureUri = new Uri(baseUri, "index.json.sig");

            var indexBytes = await Http.GetByteArrayAsync(indexUri, cancellationToken);
            var signatureText = await Http.GetStringAsync(signatureUri, cancellationToken);

            string publicKeyPem;
            var keyUri = new Uri(baseUri, "key.pem");

            if (string.IsNullOrWhiteSpace(repository.PublicKeyPem))
            {
                if (!string.IsNullOrWhiteSpace(repository.KeyFingerprint))
                {
                    return new RepositorySyncResult(
                        false,
                        "Repository key is missing; re-trust the repository.");
                }

                publicKeyPem = await Http.GetStringAsync(keyUri, cancellationToken);
            }
            else
            {
                publicKeyPem = repository.PublicKeyPem;
            }

            var fingerprint = RepositorySignature.Fingerprint(publicKeyPem);

            if (!RepositorySignature.Verify(indexBytes, signatureText, publicKeyPem, out var signatureError))
            {
                // A failed signature with a pinned key is the changed-key case. Fetch the key the
                // repository currently publishes so the user can compare both fingerprints.
                var candidate = await Http.GetStringAsync(keyUri, cancellationToken);
                var candidateFingerprint = RepositorySignature.Fingerprint(candidate);

                if (!string.Equals(candidateFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return new RepositorySyncResult(
                        false,
                        $"Repository key changed ({repository.KeyFingerprint} -> {candidateFingerprint}). Compare both fingerprints, then re-trust.",
                        PublicKeyPem: candidate,
                        Fingerprint: candidateFingerprint,
                        KeyChanged: true);
                }

                return new RepositorySyncResult(false, signatureError);
            }

            if (!string.IsNullOrWhiteSpace(repository.KeyFingerprint)
                && !string.Equals(repository.KeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return new RepositorySyncResult(
                    false,
                    $"Repository key changed ({repository.KeyFingerprint} -> {fingerprint}). Compare both fingerprints, then re-trust.",
                    PublicKeyPem: publicKeyPem,
                    Fingerprint: fingerprint,
                    KeyChanged: true);
            }

            var index = RepositoryIndexCodec.TryRead(
                System.Text.Encoding.UTF8.GetString(indexBytes),
                out var indexError);

            if (index is null)
            {
                return new RepositorySyncResult(false, indexError ?? "repository index is invalid");
            }

            if (!string.Equals(index.KeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return new RepositorySyncResult(
                    false,
                    $"Index fingerprint {index.KeyFingerprint} does not match the repository key {fingerprint}.");
            }

            WriteCache(cache, indexBytes, signatureText);
            _log.Info($"repository synced: {index.Name} ({index.Plugins.Count} plugin(s))");
            return new RepositorySyncResult(true, $"Synced {index.Name}.", index, publicKeyPem, fingerprint);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _log.Warn($"repository sync failed: {ex.Message}");
            return new RepositorySyncResult(false, "Could not reach the repository. Try again later.");
        }
    }

    public async Task<RepositoryInstallResult> InstallAsync(
        RepositorySetting repository,
        RepositoryIndex index,
        RepositoryPlugin plugin,
        CancellationToken cancellationToken = default)
    {
        if (!TryBaseUri(repository.Url, out var baseUri, out var urlError))
        {
            return new RepositoryInstallResult(false, urlError);
        }

        if (!RepositoryCompatibility.IsCompatible(plugin, CurrentVersion(), out var compatibilityError))
        {
            return new RepositoryInstallResult(false, compatibilityError);
        }

        var staging = Path.Combine(AppPaths.UserPluginsDirectory, ".staging", Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(staging, "plugin.zip");
        var extracted = Path.Combine(staging, "plugin");

        try
        {
            Directory.CreateDirectory(staging);

            var archiveUri = new Uri(baseUri, plugin.Url);
            using (var response = await Http.GetAsync(archiveUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return new RepositoryInstallResult(false, $"Archive returned {(int)response.StatusCode}.");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var destination = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination, cancellationToken);
            }

            if (!PluginArchive.VerifyPayload(archivePath, plugin.Size, plugin.Sha256, out var payloadError))
            {
                return new RepositoryInstallResult(false, payloadError);
            }

            using (var archive = File.OpenRead(archivePath))
            {
                if (!PluginArchive.TryExtract(archive, extracted, out var extractError))
                {
                    return new RepositoryInstallResult(false, extractError);
                }
            }

            var manifest = PluginLoader.ReadInstalledManifest(extracted, out var manifestErrors);
            if (manifest is null)
            {
                return new RepositoryInstallResult(false, string.Join("; ", manifestErrors));
            }

            if (!string.Equals(manifest.Id, plugin.Id, StringComparison.Ordinal)
                || !string.Equals(manifest.Kind, plugin.Kind, StringComparison.Ordinal)
                || !string.Equals(manifest.Version, plugin.Version, StringComparison.Ordinal))
            {
                return new RepositoryInstallResult(false, "plugin.json does not match the signed index entry.");
            }

            var target = Path.Combine(AppPaths.UserPluginsDirectory, manifest.Id);
            var backup = Path.Combine(AppPaths.PluginBackupsRoot, manifest.Id);

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            if (Directory.Exists(target))
            {
                Directory.CreateDirectory(AppPaths.PluginBackupsRoot);
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(extracted, target);
                PluginSourceStore.TryWrite(target, new PluginSource(
                    repository.Url,
                    repository.KeyFingerprint,
                    manifest.Version,
                    plugin.Sha256,
                    DateTimeOffset.UtcNow));
            }
            catch
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            var settings = _store.Load();
            PluginState.SetEnabled(settings, manifest.Id, false);
            _store.Save(settings);

            _log.Info($"repository installed plugin '{manifest.Id}' {manifest.Version} disabled");
            return new RepositoryInstallResult(true, $"Installed {manifest.Name}. Enable it from Plugins.", manifest.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error($"repository install failed for '{plugin.Id}'", ex);
            return new RepositoryInstallResult(false, "Could not install that plugin.");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public static bool HasBackup(string pluginId) =>
        Directory.Exists(Path.Combine(AppPaths.PluginBackupsRoot, pluginId));

    public async Task<RepositoryInstallResult> RollbackAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(AppPaths.UserPluginsDirectory, pluginId);
        var backup = Path.Combine(AppPaths.PluginBackupsRoot, pluginId);

        if (!Directory.Exists(backup))
        {
            return new RepositoryInstallResult(false, "No previous version is available.");
        }

        var current = target + ".rollback-" + Guid.NewGuid().ToString("N");

        try
        {
            if (Directory.Exists(target))
            {
                Directory.Move(target, current);
            }

            Directory.Move(backup, target);

            if (Directory.Exists(current))
            {
                Directory.Delete(current, recursive: true);
            }

            _log.Info($"rolled back plugin '{pluginId}'");
            await Task.CompletedTask;
            return new RepositoryInstallResult(true, "Previous version restored. Restart Chirpdesk.", pluginId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (!Directory.Exists(target) && Directory.Exists(current))
            {
                Directory.Move(current, target);
            }

            _log.Error($"plugin rollback failed for '{pluginId}'", ex);
            return new RepositoryInstallResult(false, "Could not restore the previous version.");
        }
    }

    private static (string Index, string Signature) CachePaths(string url)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var directory = Path.Combine(AppPaths.RepositoryCacheRoot, key[..24]);
        return (directory + ".json", directory + ".sig");
    }

    private bool TryReadCache(
        (string Index, string Signature) cache,
        RepositorySetting repository,
        out RepositorySyncResult result)
    {
        result = new RepositorySyncResult(false, string.Empty);

        if (string.IsNullOrWhiteSpace(repository.PublicKeyPem)
            || !File.Exists(cache.Index)
            || !File.Exists(cache.Signature))
        {
            return false;
        }

        try
        {
            var indexBytes = File.ReadAllBytes(cache.Index);
            var signature = File.ReadAllText(cache.Signature);

            if (!RepositorySignature.Verify(indexBytes, signature, repository.PublicKeyPem, out _))
            {
                return false;
            }

            var index = RepositoryIndexCodec.TryRead(Encoding.UTF8.GetString(indexBytes), out _);
            if (index is null
                || !string.Equals(index.KeyFingerprint, repository.KeyFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            result = new RepositorySyncResult(
                true,
                $"Using cached {index.Name} (refreshes every {CacheTtl.TotalHours:0} h).",
                index,
                repository.PublicKeyPem,
                repository.KeyFingerprint);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"repository cache read failed: {ex.Message}");
            return false;
        }
    }

    private void WriteCache((string Index, string Signature) cache, byte[] indexBytes, string signature)
    {
        try
        {
            var directory = Path.GetDirectoryName(cache.Index)!;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(cache.Index + ".tmp", indexBytes);
            File.WriteAllText(cache.Signature + ".tmp", signature);
            File.Move(cache.Index + ".tmp", cache.Index, overwrite: true);
            File.Move(cache.Signature + ".tmp", cache.Signature, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"repository cache write failed: {ex.Message}");
        }
    }

    private static bool TryBaseUri(string? value, out Uri baseUri, out string error)
    {
        baseUri = null!;
        error = string.Empty;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "repository URL must be absolute https";
            return false;
        }

        baseUri = new Uri(uri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private static Version CurrentVersion() =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"could not clean repository staging folder: {ex.Message}");
        }
    }
}
