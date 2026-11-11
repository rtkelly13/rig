using Rig.Core.Processes;

namespace Rig.Core.Tests.Estate;

/// <summary>Real git clones in a temp folder, for the estate checks to read.</summary>
internal static class Repos
{
    public static readonly SystemProc Proc = new();

    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A clone with one commit on <c>main</c>, optionally with an origin URL.</summary>
    public static async Task<string> InitAsync(string path, string? origin = null)
    {
        _ = Directory.CreateDirectory(path);
        _ = await GitAsync(path, "init", "-q", "-b", "main");
        await CommitAsync(path, "first");
        if (origin is not null)
        {
            _ = await GitAsync(path, "remote", "add", "origin", origin);
        }

        return path;
    }

    /// <summary>
    /// A clone at <c>root/r</c> of an upstream at <c>root/up</c> whose one commit ignores <paramref name="ignore"/>, so
    /// <c>main</c> is on a remote and the checkout starts clean.
    /// </summary>
    public static async Task<string> PublishedAsync(TempDir root, string ignore = "temp/\n")
    {
        var upstream = await InitAsync(root.Folder("up"));
        await File.WriteAllTextAsync(Path.Join(upstream, ".gitignore"), ignore, Token);
        await CommitAsync(upstream, "ignore");
        var repo = Path.Join(root.Path, "r");
        _ = await GitAsync(root.Path, "clone", "-q", upstream, repo);
        return repo;
    }

    /// <summary>Commits a new file named after <paramref name="message"/>.</summary>
    public static async Task CommitAsync(string path, string message)
    {
        await File.WriteAllTextAsync(Path.Join(path, $"{message}.txt"), message, Token);
        _ = await GitAsync(path, "add", "-A");
        _ = await GitAsync(path, "commit", "-q", "-m", message);
    }

    /// <summary>Runs git and asserts it succeeded; returns stdout.</summary>
    public static async Task<string> GitAsync(string path, params string[] args)
    {
        var ran = await Proc.RunAsync(new ProcRequest(["git", .. Identity, .. args]) { WorkingDirectory = path }, Token);
        Assert.True(ran.Ok, $"git {string.Join(' ', args)}: {ran.Err}");
        return ran.Out.Trim();
    }
}
