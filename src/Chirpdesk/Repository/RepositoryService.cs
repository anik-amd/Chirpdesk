using System.IO;
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
    string? Fingerprint = null);

internal sealed record RepositoryInstallResult(bool Ok, string Message, string? PluginId = null);

/// <summary>
/// HTTP and filesystem side of a static plugin repository. Every install is gated by the signed
/// index: key fingerprint, exact archive hash/size, archive policy, then a manifest that matches
/// the entry. A failure leaves the existing installed copy untouched.
/// </summary>
internal sealed class RepositoryService
{
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
            var indexUri = new Uri(baseUri, "index.json");
            var signatureUri = new Uri(baseUri, "index.json.sig");

            var indexBytes = await Http.GetByteArrayAsync(indexUri, cancellationToken);
            var signatureText = await Http.GetStringAsync(signatureUri, cancellationToken);

            string publicKeyPem;

            if (string.IsNullOrWhiteSpace(repository.PublicKeyPem))
            {
                if (!string.IsNullOrWhiteSpace(repository.KeyFingerprint))
                {
                    return new RepositorySyncResult(
                        false,
                        "Repository key is missing; re-trust the repository.");
                }

                var keyUri = new Uri(baseUri, "key.pem");
                publicKeyPem = await Http.GetStringAsync(keyUri, cancellationToken);
            }
            else
            {
                publicKeyPem = repository.PublicKeyPem;
            }

            var fingerprint = RepositorySignature.Fingerprint(publicKeyPem);

            if (!string.IsNullOrWhiteSpace(repository.KeyFingerprint)
                && !string.Equals(repository.KeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return new RepositorySyncResult(
                    false,
                    $"Repository key changed ({repository.KeyFingerprint} -> {fingerprint}). Re-trust it to continue.");
            }

            if (!RepositorySignature.Verify(indexBytes, signatureText, publicKeyPem, out var signatureError))
            {
                return new RepositorySyncResult(false, signatureError);
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
            var backup = target + ".old-" + Guid.NewGuid().ToString("N");

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.Move(extracted, target);
            }
            catch
            {
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
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
