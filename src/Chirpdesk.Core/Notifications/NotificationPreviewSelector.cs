namespace Chirpdesk.Core.Notifications;

/// <summary>
/// Picks the page preview that belongs to a notification whose body is missing.
/// Service workers on WhatsApp and Messenger do not hand WebView2 a body, so the host
/// reads the unread row from the page and matches it to the notification title.
/// Pure so the matching rules are unit-tested; the page script only collects candidates.
/// </summary>
public static class NotificationPreviewSelector
{
    /// <summary>Previews are hints, not archives; keep them short enough for one toast line.</summary>
    public const int MaxBodyLength = 400;

    public static string? Select(string? title, IReadOnlyList<PreviewCandidate> candidates)
    {
        var usable = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Body))
            .ToList();

        if (usable.Count == 0)
        {
            return null;
        }

        var wanted = Normalize(title);

        if (wanted.Length > 0)
        {
            foreach (var candidate in usable)
            {
                if (Normalize(candidate.Name) == wanted)
                {
                    return Trim(candidate.Body);
                }
            }

            foreach (var candidate in usable)
            {
                var name = Normalize(candidate.Name);
                if (name.Length > 0
                    && (name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                        || wanted.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    return Trim(candidate.Body);
                }
            }
        }

        return Trim(usable[0].Body);
    }

    private static string Normalize(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Trim(string? body)
    {
        var text = Normalize(body);
        if (text.Length == 0)
        {
            return null;
        }

        return text.Length <= MaxBodyLength ? text : text[..MaxBodyLength];
    }
}

/// <summary>One unread row as the page script saw it. Names and bodies never reach the log.</summary>
public readonly record struct PreviewCandidate(string? Name, string? Body);
