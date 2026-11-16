using System.Security.Cryptography;
using System.Text;
using Chirpdesk.Core.Time;

namespace Chirpdesk.Core.Notifications;

/// <summary>
/// Suppresses the same notification arriving twice within a short window.
/// One instance per service instance: two services may legitimately produce identical text.
/// Only a hash of the body is retained, never the body itself.
/// </summary>
public sealed class NotificationDedupe
{
    private readonly IClock _clock;
    private readonly TimeSpan _window;
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = [];

    public NotificationDedupe(IClock clock, TimeSpan window)
    {
        _clock = clock;
        _window = window;
    }

    public int Count
    {
        get
        {
            Prune(_clock.UtcNow);
            return _lastSeen.Count;
        }
    }

    public bool ShouldShow(string tag, string? title, string? body)
    {
        var now = _clock.UtcNow;
        Prune(now);

        var key = Key(tag, title, body);
        if (_lastSeen.TryGetValue(key, out var seenAt) && now - seenAt < _window)
        {
            return false;
        }

        _lastSeen[key] = now;
        return true;
    }

    public override string ToString() => $"NotificationDedupe({_lastSeen.Count} keys)";

    private static string Key(string tag, string? title, string? body)
    {
        var material = $"{tag}\u001f{title}\u001f{body}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    private void Prune(DateTimeOffset now)
    {
        if (_lastSeen.Count == 0)
        {
            return;
        }

        List<string>? expired = null;
        foreach (var (key, seenAt) in _lastSeen)
        {
            if (now - seenAt >= _window)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            _lastSeen.Remove(key);
        }
    }
}
