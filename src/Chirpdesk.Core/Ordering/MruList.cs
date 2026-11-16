namespace Chirpdesk.Core.Ordering;

/// <summary>
/// Most-recently-used order for Command Bar cycling. Pure bookkeeping: it knows ids, not windows.
/// </summary>
public sealed class MruList
{
    private readonly List<string> _items;

    public MruList(IEnumerable<string>? initial = null)
    {
        _items = initial is null ? [] : [.. initial.Distinct(StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> Items => _items;

    public void Activate(string id)
    {
        _items.Remove(id);
        _items.Insert(0, id);
    }

    public void Remove(string id) => _items.Remove(id);

    /// <summary>
    /// Next/previous id relative to <paramref name="from"/>, wrapping in both directions.
    /// An unknown or missing <paramref name="from"/> starts at the front.
    /// </summary>
    public string? Cycle(string? from, int direction)
    {
        if (_items.Count == 0)
        {
            return null;
        }

        if (from is null)
        {
            return _items[0];
        }

        var index = _items.IndexOf(from);
        if (index < 0)
        {
            return _items[0];
        }

        var next = (index + direction) % _items.Count;
        if (next < 0)
        {
            next += _items.Count;
        }

        return _items[next];
    }
}
