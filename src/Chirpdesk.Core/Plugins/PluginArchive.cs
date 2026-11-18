using System.IO.Compression;
using System.Security.Cryptography;

namespace Chirpdesk.Core.Plugins;

/// <summary>Size, count and executable rules for one plugin kind.</summary>
public sealed record PluginArchivePolicy(long MaxBytes, int MaxFiles, bool AllowExecutables)
{
    /// <summary>Data plugins: small JSON and CSS, never a program.</summary>
    public static readonly PluginArchivePolicy Data = new(5 * 1024 * 1024, 200, false);

    /// <summary>
    /// Integration helpers ship an executable and native libraries, so they get a larger budget.
    /// Only this policy allows executable entries; every other kind keeps the data rules.
    /// </summary>
    public static readonly PluginArchivePolicy Integration = new(64 * 1024 * 1024, 300, true);
}

/// <summary>
/// Archive policy for repository plugins: bounded size and count, no executable payloads (except
/// for integrations) and no path that can escape the install directory. Extraction stays inside a
/// caller-supplied temp folder; the caller validates plugin.json before moving it into the live
/// plugin root.
/// </summary>
public static class PluginArchive
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxFiles = 200;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".bat", ".cmd", ".ps1", ".msi", ".msp", ".scr", ".cpl",
        ".jar", ".vbs", ".vbe", ".js", ".mjs", ".cjs", ".lnk", ".url", ".hta",
    };

    public static PluginArchivePolicy PolicyForKind(string? kind) =>
        string.Equals(kind, PluginLoader.IntegrationKind, StringComparison.Ordinal)
            ? PluginArchivePolicy.Integration
            : PluginArchivePolicy.Data;

    public static bool IsSafeEntry(string? name, bool allowExecutables = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalized = name.Replace('\\', '/');

        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            return false;
        }

        var parts = normalized.Split('/');

        if (parts.Any(part => part is ".." or "."))
        {
            return false;
        }

        if (parts.Any(part => part.Length == 0 && !normalized.EndsWith('/')))
        {
            return false;
        }

        return allowExecutables || !ExecutableExtensions.Contains(Path.GetExtension(normalized));
    }

    /// <summary>Checks the exact signed size and SHA-256 of a downloaded archive.</summary>
    public static bool VerifyPayload(string path, long size, string sha256, out string error)
    {
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = "downloaded archive is missing";
            return false;
        }

        if (sha256 is not { Length: 64 } || !sha256.All(Uri.IsHexDigit))
        {
            error = "signed sha256 is not 64 hex characters";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);

            if (stream.Length != size)
            {
                error = $"archive size {stream.Length} does not match {size}";
                return false;
            }

            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "archive sha256 does not match the signed index";
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"could not read the archive: {ex.Message}";
            return false;
        }

        return true;
    }

    /// <summary>Inspects an archive without extracting it. Directory entries are allowed.</summary>
    public static bool TryInspect(Stream archive, out int files, out long bytes, out string error) =>
        TryInspect(archive, PluginArchivePolicy.Data, out files, out bytes, out error);

    public static bool TryInspect(
        Stream archive,
        PluginArchivePolicy policy,
        out int files,
        out long bytes,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(policy);
        files = 0;
        bytes = 0;
        error = string.Empty;

        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

            foreach (var entry in zip.Entries)
            {
                if (!IsSafeEntry(entry.FullName, policy.AllowExecutables))
                {
                    error = $"unsafe archive entry '{entry.FullName}'";
                    return false;
                }

                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                files++;
                bytes += entry.Length;

                if (files > policy.MaxFiles)
                {
                    error = $"archive has more than {policy.MaxFiles} files";
                    return false;
                }

                if (bytes > policy.MaxBytes)
                {
                    error = $"archive expands beyond {policy.MaxBytes} bytes";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            error = $"archive is not a readable zip: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Extracts a verified archive beneath <paramref name="destination"/>. The caller owns the
    /// temp folder and moves only a validated plugin folder into the live root.
    /// </summary>
    public static bool TryExtract(Stream archive, string destination, out string error) =>
        TryExtract(archive, destination, PluginArchivePolicy.Data, out error);

    public static bool TryExtract(
        Stream archive,
        string destination,
        PluginArchivePolicy policy,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(policy);
        error = string.Empty;

        try
        {
            var root = Path.GetFullPath(destination);
            Directory.CreateDirectory(root);

            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

            foreach (var entry in zip.Entries)
            {
                if (!IsSafeEntry(entry.FullName, policy.AllowExecutables))
                {
                    error = $"unsafe archive entry '{entry.FullName}'";
                    return false;
                }

                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));

                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"archive entry escapes the install folder: '{entry.FullName}'";
                    return false;
                }

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }

            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"could not extract archive: {ex.Message}";
            return false;
        }
    }
}
