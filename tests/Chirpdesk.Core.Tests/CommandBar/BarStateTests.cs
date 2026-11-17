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
    public void Search_starts_empty_so_results_can_be_filled_in()
    {
        var bar = BarState.ForSearch();

        Assert.Equal(BarMode.Search, bar.Mode);
        Assert.Empty(bar.Entries);
        Assert.Null(bar.Selected);
    }

    [Fact]
    public void Showing_results_keeps_the_selected_id_when_it_survives_filtering()
    {
        var bar = BarState.ForSearch();

        bar.ShowResults(["whatsapp", "telegram", "messenger"], keepSelected: "telegram");
        Assert.Equal("telegram", bar.Selected);

        bar.ShowResults(["telegram"], keepSelected: "telegram");
        Assert.Equal("telegram", bar.Selected);

        bar.ShowResults(["whatsapp", "messenger"], keepSelected: "telegram");
        Assert.Equal("whatsapp", bar.Selected);
    }

    [Fact]
    public void Search_accepts_more_text_and_backspace()
    {
        var bar = BarState.ForSearch();
        bar.Append("t");
        bar.Append("el");

        Assert.Equal("tel", bar.Query);

        bar.Backspace();
        Assert.Equal("te", bar.Query);
    }

    [Fact]
    public void Releasing_control_in_search_does_not_close_or_commit()
    {
        var bar = BarState.ForSearch();
        bar.ShowResults(["telegram"], keepSelected: "telegram");

        var committed = bar.ReleaseControl();

        Assert.Null(committed);
        Assert.Equal(BarMode.Search, bar.Mode);
        Assert.True(bar.IsOpen);
    }

    [Fact]
    public void Enter_with_no_results_selects_nothing()
    {
        var bar = BarState.ForSearch();
        bar.Append("zzz");
        bar.ShowResults([]);

        Assert.Null(bar.Selected);
        Assert.Null(bar.Commit());
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
