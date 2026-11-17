using Chirpdesk.Core.Storage;

namespace Chirpdesk.Core.Tests.Storage;

public class PathGuardTests
{
    private const string Root = @"C:\Users\me\AppData\Local\Chirpdesk\profiles";

    [Fact]
    public void A_child_of_the_root_is_allowed()
    {
        Assert.True(PathGuard.IsUnder(Root, Path.Combine(Root, "whatsapp", "Cache")));
        Assert.True(PathGuard.IsUnder(Root, Root));
    }

    [Fact]
    public void A_sibling_with_a_shared_prefix_is_refused()
    {
        Assert.False(PathGuard.IsUnder(Root, @"C:\Users\me\AppData\Local\Chirpdesk\profiles-backup"));
    }

    [Fact]
    public void Parent_traversal_is_resolved_and_refused()
    {
        Assert.False(PathGuard.IsUnder(Root, Path.Combine(Root, "..", "..", "Windows")));
    }

    [Fact]
    public void An_unrelated_absolute_path_is_refused()
    {
        Assert.False(PathGuard.IsUnder(Root, @"C:\Windows\System32"));
    }

    [Fact]
    public void The_check_is_case_insensitive_on_windows_paths()
    {
        Assert.True(PathGuard.IsUnder(Root.ToUpperInvariant(), Path.Combine(Root, "whatsapp")));
    }

    [Fact]
    public void Empty_or_relative_arguments_are_refused()
    {
        Assert.False(PathGuard.IsUnder("", Path.Combine(Root, "whatsapp")));
        Assert.False(PathGuard.IsUnder(Root, "whatsapp"));
    }
}
