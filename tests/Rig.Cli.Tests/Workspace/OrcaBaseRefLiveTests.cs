using System.Text.Json.Nodes;
using Rig.Core.Orca;
using Rig.Core.Processes;

namespace Rig.Cli.Tests.Workspace;

/// <summary>
/// Opt-in integration check: Orca creates a sibling worktree from the repo's configured <c>base_ref</c>, and a
/// session started in it reports the same commit as <c>origin/main</c>. Run <c>rig workspace orca sync --apply</c>
/// first, then <c>RIG_LIVE_ORCA=1 dotnet test --project tests/Rig.Cli.Tests -- --filter-class "*OrcaBaseRefLiveTests"</c>.
/// The worktree is removed afterwards, including on failure.
/// </summary>
public sealed class OrcaBaseRefLiveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_orca_worktree_and_session_start_at_the_policy_base_ref()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("RIG_LIVE_ORCA") == "1",
            "Set RIG_LIVE_ORCA=1 to create a real Orca worktree and session.");

        var proc = new SystemProc();
        var checkout = RepoRoot();
        var git = new Git(proc, Path.GetDirectoryName(Path.GetDirectoryName(checkout)) ?? checkout);
        var common = (await git.RunAsync(checkout, ["rev-parse", "--path-format=absolute", "--git-common-dir"], cancellationToken: Token)).OutOrEmpty;
        var repo = Path.GetDirectoryName(common) ?? checkout;

        var listed = await OrcaAsync(proc, Token, "repo", "list", "--json");
        Assert.True(listed.Ok, listed.Err);
        var rig = Assert.Single(OrcaRepo.ParseList(listed.OutOrEmpty) ?? [], entry => entry.Path == repo);
        Assert.Equal("origin/main", rig.BaseRef);

        var expected = (await git.RunAsync(repo, ["rev-parse", "origin/main"], cancellationToken: Token)).OutOrEmpty;
        Assert.NotEmpty(expected);

        var name = $"base-ref-check-{Guid.NewGuid():N}";
        string? worktreeId = null;
        string? terminal = null;
        try
        {
            var created = await OrcaAsync(proc, Token, "worktree", "create", "--repo", $"path:{repo}", "--name", name, "--setup", "skip", "--no-parent", "--json");
            Assert.True(created.Ok, created.Err);
            var worktree = JsonNode.Parse(created.Out)!.AsObject()["result"]!.AsObject()["worktree"]!.AsObject();
            worktreeId = (string?)worktree["id"];
            var path = (string?)worktree["path"];
            Assert.NotNull(worktreeId);
            Assert.NotNull(path);
            Assert.Equal("refs/remotes/origin/main", (string?)worktree["baseRef"]);
            Assert.Equal(expected, (string?)worktree["head"]);

            // The session must resolve its own worktree. Orca terminals inherit the launcher's environment, so a
            // GIT_DIR left over from the invoking terminal would otherwise make git read the caller's checkout.
            const string command =
                "env -u GIT_DIR -u GIT_WORK_TREE -u GIT_INDEX_FILE -u GIT_COMMON_DIR -u GIT_OBJECT_DIRECTORY "
                + "-u GIT_ALTERNATE_OBJECT_DIRECTORIES -u GIT_NAMESPACE -u GIT_PREFIX sh -c "
                + "'printf \"BASE_REF_HEAD=%s\\nBASE_REF_ORIGIN=%s\\n\" \"$(git rev-parse HEAD)\" \"$(git rev-parse origin/main)\"'";
            var opened = await OrcaAsync(proc, Token, "terminal", "create", "--worktree", $"path:{path}", "--command", command, "--json");
            Assert.True(opened.Ok, opened.Err);
            terminal = (string?)JsonNode.Parse(opened.Out)!.AsObject()["result"]!.AsObject()["terminal"]!.AsObject()["handle"];
            Assert.NotNull(terminal);

            var (head, origin) = await ReadRefsAsync(proc, terminal);
            Assert.Equal($"BASE_REF_HEAD={expected}", head);
            Assert.Equal($"BASE_REF_ORIGIN={expected}", origin);
        }
        finally
        {
            if (terminal is not null)
            {
                _ = await OrcaAsync(proc, CancellationToken.None, "terminal", "close", "--terminal", terminal, "--json");
            }

            if (worktreeId is not null)
            {
                _ = await OrcaAsync(
                    proc, CancellationToken.None, "worktree", "rm", "--worktree", $"id:{worktreeId}", "--force", "--delete-branch", "--json");
            }
        }
    }

    private static async Task<(string Head, string Origin)> ReadRefsAsync(SystemProc proc, string terminal)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var read = await OrcaAsync(proc, Token, "terminal", "read", "--terminal", terminal, "--limit", "200", "--json");
            if (read.Ok && JsonNode.Parse(read.Out) is { } json)
            {
                var lines = json["result"]?["terminal"]?["tail"]?.AsArray()
                    .Select(line => (string?)line).OfType<string>().ToList() ?? [];
                var head = lines.FirstOrDefault(line => line.StartsWith("BASE_REF_HEAD=", StringComparison.Ordinal));
                var origin = lines.FirstOrDefault(line => line.StartsWith("BASE_REF_ORIGIN=", StringComparison.Ordinal));
                if (head is not null && origin is not null)
                {
                    return (head, origin);
                }
            }

            await Task.Delay(250, Token);
        }

        Assert.Fail("The Orca session did not emit its base ref within 15 seconds.");
        return (string.Empty, string.Empty);
    }

    private static Task<Ran> OrcaAsync(SystemProc proc, CancellationToken cancellationToken, params string[] args) =>
        proc.RunAsync(new ProcRequest(["orca", .. args]) { Timeout = TimeSpan.FromSeconds(120) }, cancellationToken);

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Join(folder.FullName, "rig.slnx")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("rig.slnx is not above the test output folder.");
    }
}
