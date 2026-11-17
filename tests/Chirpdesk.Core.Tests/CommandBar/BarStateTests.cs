using Chirpdesk.Core.CommandBar;

namespace Chirpdesk.Core.Tests.CommandBar;

public class BarStateTests
{
    private static readonly string[] Warm = ["whatsapp", "telegram", "messenger"];

    [Fact]
    public void Cycle_starts_on_the_next_service_in_mru_order()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);

        Assert.Equal(BarMode.Cycle, bar.Mode);
        Assert.Equal(1, bar.Highlight);
        Assert.Equal("telegram", bar.Selected);
    }

    [Fact]
    public void Shift_cycle_starts_on_the_previous_service()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: -1);

        Assert.Equal("messenger", bar.Selected);
    }

    [Fact]
    public void Without_an_active_service_forward_opens_on_the_first_row()
    {
        var bar = BarState.ForCycle(Warm, activeId: null, direction: 1);

        Assert.Equal("whatsapp", bar.Selected);
    }

    [Fact]
    public void Highlight_wraps_forward_and_backward()
    {
        var bar = BarState.ForCycle(Warm, activeId: "messenger", direction: 1);
        Assert.Equal("whatsapp", bar.Selected);

        Assert.True(bar.Move(-1));
        Assert.Equal("messenger", bar.Selected);

        Assert.True(bar.Move(1));
        Assert.Equal("whatsapp", bar.Selected);
    }

    [Fact]
    public void A_printable_key_latches_search_and_keeps_the_highlight()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);
        bar.Move(1);

        bar.Latch("t");

        Assert.Equal(BarMode.Search, bar.Mode);
        Assert.Equal("t", bar.Query);
        Assert.Equal(2, bar.Highlight);
        Assert.Equal("messenger", bar.Selected);
    }

    [Fact]
    public void Search_accepts_more_text_and_backspace()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);
        bar.Latch("t");
        bar.Append("el");

        Assert.Equal("tel", bar.Query);

        bar.Backspace();
        Assert.Equal("te", bar.Query);
    }

    [Fact]
    public void Releasing_control_after_latching_does_not_commit()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);
        bar.Latch("t");

        var committed = bar.ReleaseControl();

        Assert.Null(committed);
        Assert.Equal(BarMode.Search, bar.Mode);
        Assert.True(bar.IsOpen);
    }

    [Fact]
    public void Releasing_control_in_cycle_commits_the_highlight()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);

        var committed = bar.ReleaseControl();

        Assert.Equal("telegram", committed);
        Assert.Equal(BarMode.Closed, bar.Mode);
        Assert.Null(bar.ReleaseControl());
    }

    [Fact]
    public void Escape_cancels_without_a_selection()
    {
        var bar = BarState.ForCycle(Warm, activeId: "whatsapp", direction: 1);

        bar.Cancel();

        Assert.Equal(BarMode.Closed, bar.Mode);
        Assert.Null(bar.ReleaseControl());
    }

    [Fact]
    public void An_empty_warm_list_is_a_no_op()
    {
        var bar = BarState.ForCycle([], activeId: "whatsapp", direction: 1);

        Assert.Equal(BarMode.Cycle, bar.Mode);
        Assert.Equal(-1, bar.Highlight);
        Assert.Null(bar.Selected);
        Assert.False(bar.Move(1));
        Assert.Null(bar.ReleaseControl());
    }

    [Fact]
    public void Duplicate_ids_do_not_create_duplicate_rows()
    {
        var bar = BarState.ForCycle(["a", "a", "b"], activeId: "a", direction: 1);

        Assert.Equal(2, bar.Entries.Count);
        Assert.Equal("b", bar.Selected);
    }
}
