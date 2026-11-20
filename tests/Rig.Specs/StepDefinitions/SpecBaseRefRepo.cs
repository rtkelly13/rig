using Rig.Core.Processes;

namespace Rig.Specs.StepDefinitions;

/// <summary>
/// A bare origin, a seed checkout that pushes to it, and a clone whose local <c>main</c> the specs move relative to
/// <c>origin/main</c>. Only <c>@real</c> scenarios use it.
/// </summary>
/// <param name="Scratch">The temp folder holding every repository.</param>
/// <param name="Proc">Runs git.</param>
/// <param name="Origin">The bare repository the clone treats as origin.</param>
/// <param name="Seed">The checkout that pushes new commits to origin/main.</param>
/// <param name="Local">The checkout the base-ref reads run in.</param>
public sealed record SpecBaseRefRepo(SpecScratch Scratch, IProc Proc, string Origin, string Seed, string Local)
{
    /// <summary>Creates the origin, one commit on main, and a clone at that commit.</summary>
    public static async Task<SpecBaseRefRepo> CreateAsync(SpecScratch scratch, IProc proc, string name)
    {
        ArgumentNullException.ThrowIfNull(scratch);
        ArgumentNullException.ThrowIfNull(proc);
        var repo = new SpecBaseRefRepo(
            scratch,
            proc,
            Path.Join(scratch.Path, $"{name}-origin.git"),
            Path.Join(scratch.Path, $"{name}-seed"),
            Path.Join(scratch.Path, $"{name}-clone"));
        _ = await repo.GitAsync(scratch.Path, "init", "-q", "--bare", "-b", "main", repo.Origin);
        _ = await repo.GitAsync(scratch.Path, "init", "-q", "-b", "main", repo.Seed);
        await File.WriteAllTextAsync(Path.Join(repo.Seed, "README.md"), "base ref specs\n");
        _ = await repo.GitAsync(repo.Seed, "add", "README.md");
        _ = await repo.GitAsync(repo.Seed, "commit", "-qm", "one");
        _ = await repo.GitAsync(repo.Seed, "remote", "add", "origin", repo.Origin);
        _ = await repo.GitAsync(repo.Seed, "push", "-q", "-u", "origin", "main");
        _ = await repo.GitAsync(scratch.Path, "clone", "-q", repo.Origin, repo.Local);
        _ = await repo.GitAsync(repo.Local, "config", "user.name", "rig-specs");
        _ = await repo.GitAsync(repo.Local, "config", "user.email", "specs@example.invalid");
        _ = await repo.GitAsync(repo.Local, "config", "commit.gpgsign", "false");
        _ = await repo.GitAsync(repo.Local, "config", "core.autocrlf", "false");
        return repo;
    }

    /// <summary>Commits <paramref name="count"/> more times in the seed and pushes them to origin/main.</summary>
    public async Task PushOriginAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await File.AppendAllTextAsync(Path.Join(Seed, "history.txt"), $"origin {index}\n");
            _ = await GitAsync(Seed, "add", ".");
            _ = await GitAsync(Seed, "commit", "-qm", $"origin {index}");
        }

        _ = await GitAsync(Seed, "push", "-q", "origin", "main");
    }

    /// <summary>Commits <paramref name="count"/> times on the clone's local main, without pushing.</summary>
    public async Task CommitLocalAsync(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await File.AppendAllTextAsync(Path.Join(Local, "local.txt"), $"local {index}\n");
            _ = await GitAsync(Local, "add", ".");
            _ = await GitAsync(Local, "commit", "-qm", $"local {index}");
        }
    }

    /// <summary>Fetches <paramref name="remotes"/> into the clone's remote-tracking refs.</summary>
    public async Task FetchAsync(params string[] remotes)
    {
        foreach (var remote in remotes)
        {
            _ = await GitAsync(Local, "fetch", "-q", remote);
        }
    }

    /// <summary>Adds <paramref name="remote"/>, fetches it, and makes it the upstream of local main.</summary>
    public async Task SetUpstreamAsync(string remote)
    {
        _ = await GitAsync(Local, "remote", "add", remote, Origin);
        _ = await GitAsync(Local, "fetch", "-q", remote);
        _ = await GitAsync(Local, "branch", "--set-upstream-to", $"{remote}/main", "main");
    }

    /// <summary>Renames local main so the policy branch no longer exists.</summary>
    public async Task RenameMainAsync() => _ = await GitAsync(Local, "branch", "-m", "main", "trunk");

    /// <summary>Deletes origin/main from the clone's remote-tracking refs, leaving local main in place.</summary>
    public async Task DeleteOriginMainAsync() => _ = await GitAsync(Local, "update-ref", "-d", "refs/remotes/origin/main");

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
