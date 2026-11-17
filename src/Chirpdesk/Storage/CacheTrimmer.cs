using System.IO;
using Chirpdesk.Core.Logging;
using Chirpdesk.Core.Storage;

namespace Chirpdesk.Storage;

/// <summary>
/// Deletes only the cache folders named by <see cref="ProfileScanner.CacheFolders"/>, only for
/// a resolved directory inside the profiles root. Session folders are never candidates.
/// </summary>
internal static class CacheTrimmer
{
    public static long Trim(TrimCandidate candidate, ILog log)
    {
        if (!PathGuard.IsUnder(AppPaths.ProfilesRoot, candidate.Directory))
        {
            log.Warn($"trim refused for '{candidate.ProfileId}': path is outside the profiles root");
            return 0;
        }

        long reclaimed = 0;

        foreach (var name in ProfileScanner.CacheFolders)
        {
            var path = Path.Combine(candidate.Directory, name);

            if (!PathGuard.IsUnder(AppPaths.ProfilesRoot, path) || !Directory.Exists(path))
            {
                continue;
            }

            var bytes = ProfileScanner.Measure(path);

            try
            {
                Directory.Delete(path, recursive: true);
                reclaimed += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.Warn($"could not trim '{name}' for '{candidate.ProfileId}': {ex.Message}");
            }
        }

        if (reclaimed > 0)
        {
            log.Info($"trimmed {reclaimed} byte(s) of cache for '{candidate.ProfileId}'");
        }

        return reclaimed;
    }
}
