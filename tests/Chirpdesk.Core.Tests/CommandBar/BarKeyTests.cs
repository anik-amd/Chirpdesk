using Chirpdesk.Core.CommandBar;

namespace Chirpdesk.Core.Tests.CommandBar;

public class BarKeyTests
{
    private static readonly string[] Warm = ["whatsapp", "telegram", "messenger"];

    private static BarState Cycle() => BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);

    private static BarState Search()
    {
        var bar = BarState.ForSearch();
        bar.ShowResults(Warm);
        return bar;
    }

    [Fact]
    public void Escape_hides_the_window()
    {
        Assert.Equal(BarKeyActionKind.Hide, BarKeys.Decide(Cycle(), BarKey.Escape, BarModifiers.None).Kind);
        Assert.Equal(BarKeyActionKind.Hide, BarKeys.Decide(Search(), BarKey.Escape, BarModifiers.None).Kind);
    }

    [Fact]
    public void Control_w_closes_the_tab()
    {
        Assert.Equal(
            BarKeyActionKind.CloseTab,
            BarKeys.Decide(Cycle(), BarKey.W, BarModifiers.Control).Kind);
        Assert.Equal(
            BarKeyActionKind.CloseTab,
            BarKeys.Decide(Search(), BarKey.W, BarModifiers.Control).Kind);
    }

    [Fact]
    public void Plain_w_is_not_a_reserved_key()
    {
        Assert.Equal(BarKeyActionKind.None, BarKeys.Decide(Cycle(), BarKey.W, BarModifiers.None).Kind);
    }

    [Theory]
    [InlineData(BarKey.Digit1, 0)]
    [InlineData(BarKey.Digit2, 1)]
    [InlineData(BarKey.Digit3, 2)]
    public void Control_digits_select_by_visible_position(BarKey key, int expected)
    {
        var action = BarKeys.Decide(Search(), key, BarModifiers.Control);

        Assert.Equal(BarKeyActionKind.SelectIndex, action.Kind);
        Assert.Equal(expected, action.Value);
    }

    [Fact]
    public void A_digit_past_the_list_does_nothing()
    {
        Assert.Equal(
            BarKeyActionKind.None,
            BarKeys.Decide(Search(), BarKey.Digit4, BarModifiers.Control).Kind);
    }

    [Fact]
    public void A_digit_without_control_is_not_reserved()
    {
        Assert.Equal(
            BarKeyActionKind.None,
            BarKeys.Decide(Search(), BarKey.Digit1, BarModifiers.None).Kind);
    }

    [Fact]
    public void Tab_and_arrows_move_the_highlight()
    {
        Assert.Equal(1, BarKeys.Decide(Cycle(), BarKey.Tab, BarModifiers.None).Value);
        Assert.Equal(-1, BarKeys.Decide(Cycle(), BarKey.Tab, BarModifiers.Shift).Value);
        Assert.Equal(-1, BarKeys.Decide(Cycle(), BarKey.Up, BarModifiers.None).Value);
        Assert.Equal(1, BarKeys.Decide(Cycle(), BarKey.Down, BarModifiers.None).Value);
    }

    [Fact]
    public void Backspace_edits_only_in_search()
    {
        Assert.Equal(
            BarKeyActionKind.Backspace,
            BarKeys.Decide(Search(), BarKey.Backspace, BarModifiers.None).Kind);
        Assert.Equal(
            BarKeyActionKind.None,
            BarKeys.Decide(Cycle(), BarKey.Backspace, BarModifiers.None).Kind);
    }

    [Fact]
    public void Enter_commits_only_in_search_and_only_with_a_row()
    {
        Assert.Equal(
            BarKeyActionKind.Commit,
            BarKeys.Decide(Search(), BarKey.Enter, BarModifiers.None).Kind);
        Assert.Equal(
            BarKeyActionKind.None,
            BarKeys.Decide(Cycle(), BarKey.Enter, BarModifiers.None).Kind);

        var empty = BarState.ForSearch();
        empty.Append("zzz");
        empty.ShowResults([]);
        Assert.Equal(
            BarKeyActionKind.None,
            BarKeys.Decide(empty, BarKey.Enter, BarModifiers.None).Kind);
    }

    [Fact]
    public void A_closed_bar_ignores_every_key()
    {
        var closed = Cycle();
        closed.Cancel();

        foreach (var key in Enum.GetValues<BarKey>())
        {
            Assert.Equal(BarKeyActionKind.None, BarKeys.Decide(closed, key, BarModifiers.Control).Kind);
        }
    }
}
