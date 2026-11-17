using System.IO;

namespace Chirpdesk.Storage;

/// <summary>Pure file measurements for one profile. Session data and cache are never mixed.</summary>
internal sealed record ProfileUsage(
    string ProfileId,
    long DataBytes,
    long CacheBytes,
    DateTimeOffset LastWrite);

internal static class ProfileScanner
{
    /// <summary>Session data. Trim must never touch these names.</summary>
    internal static readonly string[] DataFolders =
    [
        "Network", "Local Storage", "IndexedDB", "Service Worker", "Session Storage",
    ];

    /// <summary>The only reclaimable folders.</summary>
    internal static readonly string[] CacheFolders =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "ShaderCache",
    ];

    public static IReadOnlyList<ProfileUsage> ScanAll() => ScanRoot(AppPaths.ProfilesRoot);

    public static IReadOnlyList<ProfileUsage> ScanRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var usages = new List<ProfileUsage>();

        foreach (var directory in Directory.GetDirectories(root))
        {
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            usages.Add(new ProfileUsage(
                info.Name,
                MeasureUnder(directory, DataFolders),
                MeasureUnder(directory, CacheFolders),
                info.LastWriteTimeUtc));
        }

        return usages.OrderBy(usage => usage.ProfileId, StringComparer.Ordinal).ToList();
    }

    internal static long MeasureUnder(string directory, IReadOnlyList<string> names)
    {
        long total = 0;

        foreach (var name in names)
        {
            total += Measure(Path.Combine(directory, name));
        }

        return total;
    }

    internal static long Measure(string path)
    {
        if (File.Exists(path))
        {
            return new FileInfo(path).Length;
        }

        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            if (current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            try
            {
                foreach (var file in current.EnumerateFiles())
                {
                    total += file.Length;
                }

                foreach (var child in current.EnumerateDirectories())
                {
                    pending.Push(child);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file disappearing mid-scan is normal; the number is an estimate anyway.
            }
        }

        return total;
    }
}
