using Rig.Core.Processes;
using Rig.Core.Tests.Estate;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>The Drive backup against real git: a bare origin, a clone holding <c>Vault/</c>, and a stand-in Drive folder.</summary>
public sealed class DriveBackupTests : IDisposable
{
    private readonly TempDir _temp = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Plan_lists_only_notes_on_both_sides_whose_drive_copy_differs()
    {
        var (_, backup) = await ArrangeAsync(new()
        {
            ["Same.md"] = "same",
            ["Edited.md"] = "drive edit",
            ["Projects/With space.md"] = "drive edit",
            ["Drive only.md"] = "new on the phone",
            [".obsidian/Notes.md"] = "plugin state",
        });

        var plan = await backup.PlanAsync(Token);

        Assert.Equal(["Edited.md", "Projects/With space.md"], plan.Differ);
    }

    [Fact]
    public async Task Plan_is_empty_when_drive_matches_main()
    {
        var (_, backup) = await ArrangeAsync(new() { ["Same.md"] = "same", ["Edited.md"] = "edited", ["Projects/With space.md"] = "spaced" });

        var plan = await backup.PlanAsync(Token);

        Assert.Empty(plan.Differ);
    }

    [Fact]
    public async Task Push_commits_the_drive_copies_onto_main_and_leaves_the_checkout_alone()
    {
        var (clone, backup) = await ArrangeAsync(new() { ["Same.md"] = "same", ["Edited.md"] = "drive edit", ["Projects/With space.md"] = "spaced" });
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

    // main holds Same, Edited and "Projects/With space"; the Drive copy holds whatever the test gives.
    private async Task<(string Clone, DriveBackup Backup)> ArrangeAsync(Dictionary<string, string> drive)
    {
        var origin = _temp.Folder("origin.git");
        _ = await Repos.GitAsync(origin, "init", "-q", "--bare", "-b", "main");
        var clone = _temp.Folder("vault-repo");
        _ = await Repos.GitAsync(clone, "init", "-q", "-b", "main");
        _ = await Repos.GitAsync(clone, "config", "user.name", "t");
        _ = await Repos.GitAsync(clone, "config", "user.email", "t@example.invalid");
        _ = await Repos.GitAsync(clone, "config", "commit.gpgsign", "false");
        Write(Path.Join(clone, "Vault"), new() { ["Same.md"] = "same", ["Edited.md"] = "edited", ["Projects/With space.md"] = "spaced", ["Readme.txt"] = "not a note" });
        _ = await Repos.GitAsync(clone, "add", "Vault");
        _ = await Repos.GitAsync(clone, "commit", "-qm", "first");
        _ = await Repos.GitAsync(clone, "remote", "add", "origin", origin);
        _ = await Repos.GitAsync(clone, "push", "-q", "-u", "origin", "main");

        var driveVault = _temp.Folder(Path.Join("Drive placeholder", "Vault"));
        Write(driveVault, drive);
        return (clone, new DriveBackup(new Git(Repos.Proc, _temp.Path), Path.Join(clone, "Vault"), driveVault));
    }

    private static void Write(string root, Dictionary<string, string> files)
    {
        foreach (var (relative, body) in files)
        {
            var path = Path.Join(root, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }
    }
}
