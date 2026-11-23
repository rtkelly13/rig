using Rig.Core.Estate;
using Rig.Core.Roots;

namespace Rig.Core.Tests.Roots;

public sealed class WorktreeHomeTests
{
    [Fact]
    public void Worktrees_live_beside_the_clone_in_the_scope_folder()
    {
        using var root = new TempDir();
        var clone = root.Folder("personal/rig");

        Assert.Equal(Path.Join(Path.GetFullPath(root.Path), "personal", ".worktrees", "rig"), WorktreeHome.For(clone));
        Assert.Equal(WorktreeHome.For(clone), WorktreeHome.For(clone + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void An_unfinished_delete_beside_the_clone_is_left_behind()
    {
        using var root = new TempDir();
        var clone = root.Folder("personal/rig");
        var ghost = root.Folder("personal/.worktrees/rig/ghost");
        var other = root.Folder("personal/.worktrees/blog/ghost");

        var found = Decisions.LeftBehind(clone);

        Assert.Contains(ghost, found);
        Assert.DoesNotContain(other, found);
    }
}
