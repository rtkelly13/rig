using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Specs.StepDefinitions;

/// <summary>
/// A bare origin, a clone and one linked worktree whose branch is already pushed: the smallest estate that can prove a
/// shelving or removal round trip. Only <c>@real</c> scenarios use it.
/// </summary>
/// <param name="Scratch">The temp folder holding every repository.</param>
/// <param name="Proc">Runs git.</param>
/// <param name="Origin">The bare repository the clone treats as origin.</param>
/// <param name="ClonePath">The clone the commands run in.</param>
/// <param name="Tree">The linked worktree.</param>
/// <param name="Branch">The branch the worktree has checked out.</param>
/// <param name="Canary">The blob id of the worktree's first commit, as origin holds it.</param>
public sealed record SpecRepo(SpecScratch Scratch, IProc Proc, string Origin, string ClonePath, string Tree, string Branch, string Canary)
{
    /// <summary>Creates the estate and commits and pushes one file in the worktree.</summary>
    public static async Task<SpecRepo> CreateAsync(SpecScratch scratch, IProc proc, string branch, string file = "work.md", string content = "canary, committed and pushed\n")
    {
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(proc);
        var repo = new SpecRepo(scratch, proc, Path.Join(scratch.Path, "origin.git"), Path.Join(scratch.Path, "clone"), Path.Join(scratch.Path, "clone", "temp", branch), branch, string.Empty);
        _ = await repo.GitAsync(scratch.Path, "init", "-q", "--bare", "-b", "main", repo.Origin);
        var seed = scratch.Folder("seed");
        _ = await repo.GitAsync(seed, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Join(seed, ".gitignore"), "node_modules/\nTODO.md\n");
        await File.WriteAllTextAsync(Path.Join(seed, "README.md"), "rig specs\n");
        _ = await repo.GitAsync(seed, "add", ".");
        _ = await repo.GitAsync(seed, "commit", "-qm", "first");
        _ = await repo.GitAsync(seed, "remote", "add", "origin", repo.Origin);
        _ = await repo.GitAsync(seed, "push", "-q", "-u", "origin", "main");
        _ = await repo.GitAsync(scratch.Path, "clone", "-q", repo.Origin, repo.ClonePath);
        // The clone commits on its own (a shelve marker), so it needs an identity: CI runners have no global one.
        _ = await repo.GitAsync(repo.ClonePath, "config", "user.name", "rig-specs");
        _ = await repo.GitAsync(repo.ClonePath, "config", "user.email", "specs@example.invalid");
        _ = await repo.GitAsync(repo.ClonePath, "config", "commit.gpgsign", "false");
        _ = await repo.GitAsync(repo.ClonePath, "config", "core.autocrlf", "false");
        _ = await repo.GitAsync(repo.ClonePath, "worktree", "add", "-q", "-b", branch, repo.Tree);
        await File.WriteAllTextAsync(Path.Join(repo.Tree, file), content);
        _ = await repo.GitAsync(repo.Tree, "add", ".");
        _ = await repo.GitAsync(repo.Tree, "commit", "-qm", "spike work");
        _ = await repo.GitAsync(repo.Tree, "push", "-q", "-u", "origin", branch);
        var canary = await repo.GitAsync(repo.Origin, "rev-parse", $"{branch}^{{tree}}:{file}");
        return repo with { Canary = canary };
    }

    /// <summary>The <see cref="Worktree"/> git lists for <see cref="Tree"/>.</summary>
    public async Task<Worktree> RegisteredAsync()
    {
        var trees = await Worktree.ListAsync(Git(ClonePath), ClonePath);
        return trees.FirstOrDefault(candidate => PathResolution.Same(PathResolution.Resolve(candidate.Path), PathResolution.Resolve(Tree)))
            ?? throw new InvalidOperationException($"git worktree list did not report {Tree}");
    }

    /// <summary>Runs git in <paramref name="cwd"/> with an identity, asserting success.</summary>
    public async Task<string> GitAsync(string cwd, params string[] args)
    {
        var ran = await Git(cwd).RunAsync(
            cwd,
            ["-c", "user.name=rig-specs", "-c", "user.email=specs@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false", .. args],
            TimeSpan.FromSeconds(60));
        Assert.True(ran.Ok, $"git {string.Join(' ', args)} in {cwd}: {ran.Err}");
        return ran.Out.Trim();
    }

    /// <summary>A git runner rooted at the scratch folder, so no path is treated as untrusted.</summary>
    public Git Git(string cwd) => new(Proc, Scratch.Path);
}
