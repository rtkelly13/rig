using Rig.Core.Processes;
using Rig.Core.Tests.Estate;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>The Drive backup against real git (<see cref="DriveVaults"/>).</summary>
public sealed class DriveBackupTests : IDisposable
{
    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Plan_lists_only_notes_on_both_sides_whose_drive_copy_differs()
    {
        var backup = await ArrangeAsync(new()
        {
            ["Same.md"] = "same",
            ["Edited.md"] = "drive edit",
            ["Projects/With space.md"] = "drive edit",
            ["Drive only.md"] = "new on the phone",
            [".obsidian/Notes.md"] = "plugin state",
        });

        var plan = await backup.PlanAsync(Token);

        Assert.Equal(3, plan.Shared);
        Assert.Equal(["Edited.md", "Projects/With space.md"], plan.Differ);
        Assert.Empty(plan.Unreadable);
    }

    [Fact]
    public async Task Plan_is_empty_when_drive_matches_main()
    {
        var backup = await ArrangeAsync(new() { ["Same.md"] = "same", ["Edited.md"] = "edited", ["Projects/With space.md"] = "spaced" });

        var plan = await backup.PlanAsync(Token);

        Assert.Equal(3, plan.Shared);
        Assert.Empty(plan.Differ);
    }

    [Fact]
    public async Task Plan_skips_a_drive_note_that_will_not_open()
    {
        var backup = await ArrangeAsync(new() { ["Same.md"] = "same", ["Projects/With space.md"] = "drive edit" });
        File.CreateSymbolicLink(Path.Join(_temp.Path, "Drive placeholder", "Vault", "Edited.md"), Path.Join(_temp.Path, "gone.md"));

        var plan = await backup.PlanAsync(Token);

        Assert.Equal(["Projects/With space.md"], plan.Differ);
        Assert.Equal(["Edited.md"], plan.Unreadable);
    }

    [Fact]
    public async Task Push_commits_the_drive_copies_onto_main_and_leaves_the_checkout_alone()
    {
        var backup = await ArrangeAsync(new() { ["Same.md"] = "same", ["Edited.md"] = "drive edit", ["Projects/With space.md"] = "spaced" });
        var clone = Path.Join(_temp.Path, "vault-repo");
        await File.WriteAllTextAsync(Path.Join(clone, "Vault", "Same.md"), "uncommitted local work", Token);
        var head = await Repos.GitAsync(clone, "rev-parse", "HEAD");
        var plan = await backup.PlanAsync(Token);

        var pushed = await backup.PushAsync(plan, "backup/drive-diff-test", "backup", Token);

        var origin = Path.Join(_temp.Path, "origin.git");
        Assert.Equal(pushed.Commit, await Repos.GitAsync(origin, "rev-parse", "backup/drive-diff-test"));
        Assert.Equal(plan.Base, await Repos.GitAsync(origin, "rev-parse", $"{pushed.Commit}^"));
        Assert.Equal("Vault/Edited.md", await Repos.GitAsync(origin, "diff", "--name-only", plan.Base, pushed.Commit));
        Assert.Equal("drive edit", await Repos.GitAsync(origin, "show", $"{pushed.Commit}:Vault/Edited.md"));
        Assert.Equal(head, await Repos.GitAsync(clone, "rev-parse", "HEAD"));
        // GitAsync trims, so the porcelain " M" loses its leading space.
        Assert.Equal("M Vault/Same.md", await Repos.GitAsync(clone, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_git_failure_never_names_the_drive_folder()
    {
        var backup = await ArrangeAsync(new() { ["Edited.md"] = "drive edit" });
        var plan = await backup.PlanAsync(Token);
        File.Delete(Path.Join(_temp.Path, "Drive placeholder", "Vault", "Edited.md"));

        var failure = await Assert.ThrowsAsync<DriveBackupException>(() => backup.PushAsync(plan, "backup/drive-diff-test", "backup", Token));

        Assert.Contains("<drive>", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Drive placeholder", failure.Message, StringComparison.Ordinal);
    }

    private async Task<DriveBackup> ArrangeAsync(Dictionary<string, string> drive)
    {
        var (_, vault, driveVault) = await DriveVaults.ArrangeAsync(_temp, drive);
        return new DriveBackup(new Git(Repos.Proc, _temp.Path), vault, driveVault);
    }
}
