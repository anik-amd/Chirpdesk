using System.IO;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Storage;
using Chirpdesk.Interop;

namespace Chirpdesk.Storage;

/// <summary>
/// Deletes one scanned orphan after re-verifying it against the same fixed allowlist. Known
/// app roots are deleted as the root itself; Chirpdesk profiles only as a direct child of the
/// profiles root. Nothing is pre-deleted by the scanner.
/// </summary>
internal static class OrphanDeleter
{
    public static bool Delete(OrphanItem item, ILog log)
    {
        if (item.IsChirpdeskProfile)
        {
            var parent = Path.GetDirectoryName(item.Path);
            if (parent is null
                || !string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.ProfilesRoot)),
                    StringComparison.OrdinalIgnoreCase))
            {
                log.Warn("orphan delete refused: profile is not a direct child of the profiles root");
                return false;
            }
        }
        else if (!KnownRoots.IsKnownRoot(item.Path))
        {
            log.Warn("orphan delete refused: path is not a known root");
            return false;
        }

        return RecycleBin.Send(item.Path, log);
    }
}
