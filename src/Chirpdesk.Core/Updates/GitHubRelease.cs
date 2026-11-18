using System.Text.Json;

namespace Chirpdesk.Core.Updates;

/// <summary>
/// Reads the stable-release envelope GitHub returns. The signed update manifest is one asset
/// inside it; this parser never trusts the release body, tag or asset URL beyond picking the
/// manifest URL. The signature check still decides whether anything is installable.
/// </summary>
public sealed record GitHubRelease(string Tag, string ManifestUrl);

public static class GitHubReleaseParser
{
    public const string ManifestAssetName = "update.json";

    public static bool TryReadLatest(string? json, out GitHubRelease? release, out string error)
    {
        release = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "release response is empty";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "release response is not an object";
                return false;
            }

            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            {
                error = "latest release is a draft";
                return false;
            }

            if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True)
            {
                error = "latest release is a prerelease";
                return false;
            }

            if (!root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tagElement.GetString()))
            {
                error = "release has no tag_name";
                return false;
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                error = "release has no assets";
                return false;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !asset.TryGetProperty("name", out var name)
                    || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString(), ManifestAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!asset.TryGetProperty("browser_download_url", out var url)
                    || url.ValueKind != JsonValueKind.String
                    || !UpdateVerifier.IsAbsoluteHttps(url.GetString(), out error))
                {
                    error = "update.json asset has no absolute https URL";
                    return false;
                }

                release = new GitHubRelease(tagElement.GetString()!.Trim(), url.GetString()!.Trim());
                return true;
            }

            error = $"release has no {ManifestAssetName} asset";
            return false;
        }
        catch (JsonException ex)
        {
            error = $"release response is not valid json: {ex.Message}";
            return false;
        }
    }
}
