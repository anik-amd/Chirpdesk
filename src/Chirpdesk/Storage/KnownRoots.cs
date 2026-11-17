using System.IO;
using Chirpdesk.Core.Storage;

namespace Chirpdesk.Storage;

/// <summary>
/// The only roots the orphan scan may report. Each entry is a literal path under the user
/// profile (or a Store package matched inside the fixed Packages root); no globbing elsewhere.
/// </summary>
internal static class KnownRoots
{
    private const string WhatsAppStorePrefix = "5319275A.WhatsAppDesktop_";

    public static IReadOnlyList<ScanRoot> ForThisUser()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<ScanRoot>();

        Add(roots, Path.Combine(roaming, "WhatsApp"), "WhatsApp");
        Add(roots, Path.Combine(local, "WhatsApp"), "WhatsApp");

        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var package in Directory.GetDirectories(packages, WhatsAppStorePrefix + "*"))
            {
                Add(roots, Path.Combine(package, "LocalState"), "WhatsApp");
            }
        }

        Add(roots, Path.Combine(roaming, "Telegram Desktop"), "Telegram");
        Add(roots, Path.Combine(roaming, "Ferdium"), "Ferdium");
        Add(roots, Path.Combine(roaming, "Rambox"), "Rambox");
        Add(roots, Path.Combine(roaming, "Station"), "Station");
        Add(roots, Path.Combine(roaming, "MultiMessenger"), "MultiMessenger");

        return roots;
    }

    public static bool IsKnownRoot(string path) =>
        ForThisUser().Any(root => string.Equals(root.Path, path, StringComparison.OrdinalIgnoreCase));

    public static bool IsInUse(string path)
    {
        var root = ForThisUser().FirstOrDefault(
            candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));

        return root is not null && ProcessProbe.IsRunning(root.App);
    }

    private static void Add(List<ScanRoot> roots, string path, string app)
    {
        if (Directory.Exists(path))
        {
            roots.Add(new ScanRoot(path, app));
        }
    }
}
