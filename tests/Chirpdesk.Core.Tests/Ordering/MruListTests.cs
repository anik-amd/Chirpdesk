using Chirpdesk.Core.Ordering;

namespace Chirpdesk.Core.Tests.Ordering;

public class MruListTests
{
    [Fact]
    public void Activation_moves_an_id_to_the_front()
    {
        var mru = new MruList(["a", "b", "c"]);
        mru.Activate("c");
        Assert.Equal(["c", "a", "b"], mru.Items);
    }

    [Fact]
    public void Activation_does_not_duplicate_an_id()
    {
        var mru = new MruList(["a", "b"]);
        mru.Activate("a");
        mru.Activate("a");
        Assert.Equal(["a", "b"], mru.Items);
    }

    [Fact]
    public void An_unknown_id_is_inserted_at_the_front()
    {
        var mru = new MruList(["a"]);
        mru.Activate("z");
        Assert.Equal(["z", "a"], mru.Items);
    }

    [Fact]
    public void Cycling_forward_wraps()
    {
        var mru = new MruList(["a", "b", "c"]);
        Assert.Equal("b", mru.Cycle("a", 1));
        Assert.Equal("c", mru.Cycle("b", 1));
        Assert.Equal("a", mru.Cycle("c", 1));
    }

    [Fact]
    public void Cycling_backward_wraps()
    {
        var mru = new MruList(["a", "b", "c"]);
        Assert.Equal("c", mru.Cycle("a", -1));
        Assert.Equal("a", mru.Cycle("b", -1));
    }

    [Fact]
    public void Cycling_from_nothing_or_an_unknown_id_starts_at_the_front()
    {
        var mru = new MruList(["a", "b"]);
        Assert.Equal("a", mru.Cycle(null, 1));
        Assert.Equal("a", mru.Cycle("nope", 1));
    }

    [Fact]
    public void An_empty_list_cycles_to_null()
    {
        var mru = new MruList();
        Assert.Null(mru.Cycle(null, 1));
        Assert.Empty(mru.Items);
    }

    [Fact]
    public void Removed_ids_disappear_without_reordering_the_rest()
    {
        var mru = new MruList(["a", "b", "c"]);
        mru.Remove("b");
        Assert.Equal(["a", "c"], mru.Items);
        Assert.Equal("c", mru.Cycle("a", 1));
    }
}
