namespace Chirpdesk.Core.CommandBar;

public enum BarMode
{
    Closed,
    Cycle,
    Search,
    Tabs,
}

/// <summary>
/// The Command Bar's mode, highlight and query, with no WPF in it. Cycle starts on the next
/// service in MRU order; Ctrl+K opens search directly; Ctrl+Shift+K opens the sticky tab list;
/// only a Ctrl release while still in cycle mode commits (ADR 0002).
/// </summary>
public sealed class BarState
{
    private readonly List<string> _entries;
    private int _highlight = -1;

    private BarState(IReadOnlyList<string> entries)
    {
        _entries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static BarState ForCycle(IReadOnlyList<string> entries, string? activeId, int direction)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var state = new BarState(entries) { Mode = BarMode.Cycle };
        state._highlight = activeId is null ? -1 : state._entries.IndexOf(activeId);
        state.Move(direction);
        return state;
    }

    /// <summary>Ctrl+Shift+K opens the sticky list of open tabs, highlighted on the active one.</summary>
    public static BarState ForTabs(IReadOnlyList<string> entries, string? activeId)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var state = new BarState(entries) { Mode = BarMode.Tabs };
        var index = activeId is null ? -1 : state._entries.IndexOf(activeId);
        state._highlight = index >= 0 ? index : (state._entries.Count > 0 ? 0 : -1);
        return state;
    }

    /// <summary>Ctrl+K opens directly here; results are filled in as the query changes.</summary>
    public static BarState ForSearch() => new([]) { Mode = BarMode.Search };

    /// <summary>
    /// Replaces the visible rows. The highlight follows <paramref name="keepSelected"/> when
    /// that row survived the filter, otherwise it lands on the first row.
    /// </summary>
    public void ShowResults(IReadOnlyList<string> entries, string? keepSelected = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _entries.Clear();
        _entries.AddRange(entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Distinct(StringComparer.Ordinal));

        var index = keepSelected is null ? -1 : _entries.IndexOf(keepSelected);
        _highlight = index >= 0 ? index : (_entries.Count > 0 ? 0 : -1);
    }

    public BarMode Mode { get; private set; }

    public IReadOnlyList<string> Entries => _entries;

    public int Highlight => _highlight;

    public string? Selected =>
        _highlight >= 0 && _highlight < _entries.Count ? _entries[_highlight] : null;

    public string Query { get; private set; } = string.Empty;

    /// <summary>Search text comes from the real text field, not from synthetic key handling.</summary>
    public void SetQuery(string text)
    {
        if (Mode == BarMode.Search)
        {
            Query = text ?? string.Empty;
        }
    }

    public bool IsOpen => Mode != BarMode.Closed;

    /// <summary>Moves the highlight by one row, wrapping. Returns false when there is no row.</summary>
    public bool Move(int direction)
    {
        if (_entries.Count == 0)
        {
            return false;
        }

        var step = direction < 0 ? -1 : 1;
        _highlight = _highlight < 0
            ? (step > 0 ? 0 : _entries.Count - 1)
            : Mod(_highlight + step, _entries.Count);

        return true;
    }

    public void Append(string text)
    {
        if (Mode == BarMode.Search && !string.IsNullOrEmpty(text))
        {
            Query += text;
        }
    }

    public void Backspace()
    {
        if (Mode == BarMode.Search && Query.Length > 0)
        {
            Query = Query[..^1];
        }
    }

    /// <summary>
    /// Ctrl released. Commits only in cycle mode; an open search bar stays open with no switch.
    /// </summary>
    public string? ReleaseControl()
    {
        if (Mode != BarMode.Cycle)
        {
            return null;
        }

        return Commit();
    }

    /// <summary>Closes the bar and returns the highlighted service, if any.</summary>
    public string? Commit()
    {
        var selected = Selected;
        Mode = BarMode.Closed;
        return selected;
    }

    public void Cancel() => Mode = BarMode.Closed;

    private static int Mod(int value, int count) => ((value % count) + count) % count;
}
