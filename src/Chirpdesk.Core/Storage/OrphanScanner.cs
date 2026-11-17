using System.IO;
using System.IO.Enumeration;

namespace Chirpdesk.Core.Storage;

/// <summary>A fixed root that may hold another app's leftover data. Never a glob.</summary>
public sealed record ScanRoot(string Path, string App);

public sealed record OrphanItem(
    string? ProfileId,
    string Path,
    string App,
    long Bytes,
    DateTimeOffset LastWrite,
    bool IsChirpdeskProfile);

/// <summary>
/// Finds data that no settings entry references. It never invents roots: the caller supplies
/// the fixed allowlist, and a profile is only reported when its id is not referenced.
/// </summary>
public static class OrphanScanner
{
    public static IReadOnlyList<OrphanItem> Scan(
        string profilesRoot,
        IReadOnlySet<string> referencedProfileIds,
        IEnumerable<ScanRoot> roots,
        Func<string, bool> isInUse)
    {
        ArgumentNullException.ThrowIfNull(referencedProfileIds);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(isInUse);

        var items = new List<OrphanItem>();

        if (Directory.Exists(profilesRoot))
        {
            foreach (var directory in Directory.GetDirectories(profilesRoot).Order(StringComparer.Ordinal))
            {
                var info = new DirectoryInfo(directory);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || referencedProfileIds.Contains(info.Name)
                    || isInUse(directory))
                {
                    continue;
                }

                items.Add(new OrphanItem(
                    info.Name,
                    directory,
                    "Chirpdesk profile",
                    Measure(directory),
                    info.LastWriteTimeUtc,
                    IsChirpdeskProfile: true));
            }
        }

        foreach (var root in roots)
        {
            if (!Path.IsPathFullyQualified(root.Path) || !Directory.Exists(root.Path))
            {
                continue;
            }

            var info = new DirectoryInfo(root.Path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || isInUse(root.Path))
            {
                continue;
            }

            items.Add(new OrphanItem(
                null,
                root.Path,
                root.App,
                Measure(root.Path),
                info.LastWriteTimeUtc,
                IsChirpdeskProfile: false));
        }

        return items;
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
                // Files vanishing during a scan are normal; the total is an estimate.
            }
        }

        return total;
    }
}
