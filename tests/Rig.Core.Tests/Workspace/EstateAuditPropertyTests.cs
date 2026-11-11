using CsCheck;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Core.Tests.Workspace;

/// <summary>
/// Properties of <see cref="EstateAudit"/> over random estates of real git clones: every entry is classified once,
/// a dry run changes nothing, and cleaning removes exactly the unmanaged items it may, never a declared, retained or
/// dirty one. Replaces <c>tests/test_estate_sync_state_machine.py</c>'s audit and clean rules.
/// </summary>
public sealed class EstateAuditPropertyTests
{
    private enum Entry
    {
        Managed,
        Mismatched,
        StrayClean,
        StrayDirty,
        File,
        Folder,
        Worktree,
        Retained,
        System,
    }

    private static readonly EstateScopes Scopes = new(["o"], []);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Audit_classifies_every_entry_once_and_clean_removes_only_what_it_may()
    {
        var gen = Gen.Enum<Entry>().Array[0, 7];
        await gen.SampleAsync(CheckAsync, iter: 20, threads: 1);
    }

    private static async Task CheckAsync(Entry[] entries)
    {
        using var root = new TempDir();
        var proc = new SystemProc();
        var estate = root.Folder("personal");
        var specs = new List<ManagedRepoSpec>();
        var system = EstateAudit.KnownSystemEntries.Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < entries.Length; i++)
        {
            var name = entries[i] == Entry.System ? system[i] : $"e{i}";
            var path = Path.Join(estate, name);
            switch (entries[i])
            {
                case Entry.Managed:
                    await CloneAsync(proc, path, $"git@github.com:o/{name}.git");
                    specs.Add(new(name, $"git@github.com:o/{name}.git"));
                    break;
                case Entry.Mismatched:
                    await CloneAsync(proc, path, $"git@github.com:x/{name}.git");
                    specs.Add(new(name, $"git@github.com:o/{name}.git"));
                    break;
                case Entry.StrayClean:
                    await CloneAsync(proc, path, null);
                    break;
                case Entry.StrayDirty:
                    await CloneAsync(proc, path, null);
                    await File.WriteAllTextAsync(Path.Join(path, "work.txt"), "unsaved", Token);
                    break;
                case Entry.File:
                    await File.WriteAllTextAsync(path, "x", Token);
                    break;
                case Entry.Folder:
                    _ = root.File(Path.Join("personal", name, "a.txt"));
                    break;
                case Entry.Worktree:
                    _ = root.File(Path.Join("personal", name, ".git"));
                    break;
                case Entry.Retained:
                    _ = root.File(Path.Join("personal", name, "kept.txt"));
                    specs.Add(new(name, string.Empty, Enabled: false));
                    break;
                default:
                    _ = root.File(Path.Join("personal", name, "x"));
                    break;
            }
        }

        var audit = new EstateAudit(new Git(proc, root.Path), Scopes);
        var before = await audit.RunAsync(estate, specs, new HashSet<string>(), Token);

        EstateItem[] classified = [.. before.ManagedRepos, .. before.LinkedWorktrees, .. before.UnmanagedItems, .. before.KnownSystemItems, .. before.RetainedArchives];
        Assert.Equal(entries.Length, before.TotalItems);
        Assert.Equal(Directory.EnumerateFileSystemEntries(estate).Order(StringComparer.Ordinal), classified.Select(item => item.Path).Order(StringComparer.Ordinal));
        Assert.Empty(before.MissingManagedRepos);
        Assert.Empty(before.BlockedManagedRepos);
        Assert.Equal(entries.Count(e => e is Entry.Mismatched or Entry.StrayClean or Entry.StrayDirty or Entry.File or Entry.Folder), before.UnmanagedItems.Count);
        Assert.Equal(before.UnmanagedItems.Count == 0, before.IsClean);

        foreach (var item in before.UnmanagedItems)
        {
            Assert.True((await audit.CleanAsync(item, dryRun: true, Token)).Ok);
        }

        Assert.Equal(before, await audit.RunAsync(estate, specs, new HashSet<string>(), Token), Same);

        foreach (var item in before.UnmanagedItems)
        {
            _ = await audit.CleanAsync(item, dryRun: false, Token);
        }

        var after = await audit.RunAsync(estate, specs, new HashSet<string>(), Token);
        Assert.Equal(before.ManagedRepos, after.ManagedRepos);
        Assert.Equal(before.LinkedWorktrees, after.LinkedWorktrees);
        Assert.Equal(before.KnownSystemItems, after.KnownSystemItems);
        Assert.Equal(before.RetainedArchives, after.RetainedArchives);
        Assert.All(after.RetainedArchives, item => Assert.True(File.Exists(Path.Join(item.Path, "kept.txt"))));

        // Only the dirty clones survive a clean; the mismatched clones were committed-clean, so they go.
        Assert.Equal(entries.Count(e => e == Entry.StrayDirty), after.UnmanagedItems.Count);
        Assert.All(after.UnmanagedItems, item => Assert.True(File.Exists(Path.Join(item.Path, "work.txt"))));
    }

    private static bool Same(EstateAuditResult a, EstateAuditResult b) =>
        a.TotalItems == b.TotalItems
        && a.ManagedRepos.SequenceEqual(b.ManagedRepos)
        && a.UnmanagedItems.SequenceEqual(b.UnmanagedItems)
        && a.RetainedArchives.SequenceEqual(b.RetainedArchives)
        && a.KnownSystemItems.SequenceEqual(b.KnownSystemItems)
        && a.LinkedWorktrees.SequenceEqual(b.LinkedWorktrees);

    private static async Task CloneAsync(SystemProc proc, string path, string? remote)
    {
        _ = Directory.CreateDirectory(path);
        await GitAsync(proc, path, "init", "-q");
        if (remote is not null)
        {
            await GitAsync(proc, path, "remote", "add", "origin", remote);
        }
    }

    private static async Task GitAsync(SystemProc proc, string path, params string[] args)
    {
        var ran = await proc.RunAsync(new ProcRequest(["git", .. args]) { WorkingDirectory = path }, Token);
        Assert.True(ran.Ok, ran.Err);
    }
}
