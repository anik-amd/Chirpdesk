namespace Chirpdesk.Core.CommandBar;

/// <summary>Keys the bar understands. Everything else falls through to text input.</summary>
public enum BarKey
{
    None,
    Tab,
    Up,
    Down,
    Escape,
    Enter,
    Backspace,
    W,
    Digit1,
    Digit2,
    Digit3,
    Digit4,
    Digit5,
    Digit6,
    Digit7,
    Digit8,
    Digit9,
}

[Flags]
public enum BarModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
}

public enum BarKeyActionKind
{
    None,
    Move,
    Backspace,
    Commit,
    Cancel,
    CloseTab,
    SelectIndex,
}

/// <summary>What the host should do for one key. Pure; the caller applies it.</summary>
public readonly record struct BarKeyAction(BarKeyActionKind Kind, int Value = 0)
{
    public static BarKeyAction None => new(BarKeyActionKind.None);
}

/// <summary>
/// Reserved keys while the bar is open. Kept out of WPF so every combination is unit-tested;
/// the window maps keys in and executes the action (ADR 0002).
/// </summary>
public static class BarKeys
{
    public static BarKeyAction Decide(BarState bar, BarKey key, BarModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(bar);

        if (!bar.IsOpen)
        {
            return BarKeyAction.None;
        }

        var control = modifiers.HasFlag(BarModifiers.Control);
        var shift = modifiers.HasFlag(BarModifiers.Shift);

        if (key == BarKey.Escape)
        {
            return new BarKeyAction(BarKeyActionKind.Cancel);
        }

        if (control && key == BarKey.W)
        {
            return new BarKeyAction(BarKeyActionKind.CloseTab);
        }

        if (control && Digit(key, out var index))
        {
            return index < bar.Entries.Count
                ? new BarKeyAction(BarKeyActionKind.SelectIndex, index)
                : BarKeyAction.None;
        }

        if (key == BarKey.Tab)
        {
            return new BarKeyAction(BarKeyActionKind.Move, shift ? -1 : 1);
        }

        if (key == BarKey.Up)
        {
            return new BarKeyAction(BarKeyActionKind.Move, -1);
        }

        if (key == BarKey.Down)
        {
            return new BarKeyAction(BarKeyActionKind.Move, 1);
        }

        if (key == BarKey.Backspace && bar.Mode == BarMode.Search)
        {
            return new BarKeyAction(BarKeyActionKind.Backspace);
        }

        if (key == BarKey.Enter
            && bar.Mode is BarMode.Search or BarMode.Tabs
            && bar.Selected is not null)
        {
            return new BarKeyAction(BarKeyActionKind.Commit);
        }

        return BarKeyAction.None;
    }

    private static bool Digit(BarKey key, out int index)
    {
        var value = (int)key;

        if (value < (int)BarKey.Digit1 || value > (int)BarKey.Digit9)
        {
            index = -1;
            return false;
        }

        index = value - (int)BarKey.Digit1;
        return true;
    }
}
