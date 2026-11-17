using System.IO;
using Chirpdesk.Core.Definitions;
using Chirpdesk.Core.Logging;

namespace Chirpdesk.Definitions;

/// <summary>
/// Shipped definitions live as embedded JSON so a portable copy cannot lose them, and are
/// validated through the same path a plugin will use later. Invalid ones are skipped and
/// logged; having no definitions still leaves the app usable with custom sites.
/// </summary>
internal static class ShippedDefinitions
{
    private const string ResourcePrefix = "Chirpdesk.Definitions.";

    public static ServiceCatalog Load(ILog log)
    {
        var assembly = typeof(ShippedDefinitions).Assembly;
        var documents = new List<string>();

        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                log.Warn($"definition resource missing: {name}");
                continue;
            }

            using var reader = new StreamReader(stream);
            documents.Add(reader.ReadToEnd());
        }

        var errors = new List<string>();
        var definitions = DefinitionLoader.Load(documents, errors);

        foreach (var error in errors)
        {
            log.Warn($"definition skipped: {error}");
        }

        log.Info($"definitions: {definitions.Count} loaded, {errors.Count} skipped");
        return new ServiceCatalog(definitions);
    }
}
