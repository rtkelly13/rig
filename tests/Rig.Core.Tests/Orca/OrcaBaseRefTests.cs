using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Orca;

/// <summary><see cref="OrcaBaseRef"/>: reading each repo's local branch against its remote-tracking ref.</summary>
public sealed class OrcaBaseRefTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Counts_parse_ahead_then_behind()
    {
        Assert.Equal(new LocalBaseState("main", "origin", 2, 3), OrcaBaseRef.Parse("main", "origin", "2\t3\n"));
        Assert.Equal(new LocalBaseState("main", "origin", 0, 0), OrcaBaseRef.Parse("main", "origin", "0\t0\n"));
        Assert.Null(OrcaBaseRef.Parse("main", "origin", ""));
        Assert.Null(OrcaBaseRef.Parse("main", "origin", "2\tbehind"));
        Assert.Null(OrcaBaseRef.Parse("main", "origin", "2\t3\textra"));
    }

    [Fact]
    public async Task It_reads_each_repos_ahead_and_behind_counts()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(0, "abc\n", string.Empty), "git", "rev-parse", "--verify", "--quiet", "refs/heads/main")
            .AnswerIn("/repos/rig", new Ran(0, "def\n", string.Empty), "git", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/main")
            .AnswerIn("/repos/rig", new Ran(0, "2\t3\n", string.Empty), "git", "rev-list", "--count", "--left-right", "main...origin/main");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        var state = Assert.Single(states);
        Assert.Equal("rig", state.Key);
        Assert.Equal(new LocalBaseState("main", "origin", 2, 3), state.Value);
    }

    [Fact]
    public async Task A_repo_without_the_local_branch_is_skipped()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(1, string.Empty, string.Empty), "git", "rev-parse", "--verify", "--quiet", "refs/heads/main");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        Assert.Empty(states);
        Assert.Single(proc.Requests);
    }

    [Fact]
    public async Task A_repo_without_the_remote_tracking_ref_is_skipped()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(0, "abc\n", string.Empty), "git", "rev-parse", "--verify", "--quiet", "refs/heads/main")
            .AnswerIn("/repos/rig", new Ran(1, string.Empty, string.Empty), "git", "rev-parse", "--verify", "--quiet", "refs/remotes/origin/main");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        Assert.Empty(states);
        Assert.DoesNotContain(proc.Requests, request => request.Argv.Contains("rev-list"));
    }

    [Fact]
    public async Task A_base_ref_naming_no_branch_is_not_read()
    {
        var proc = new FakeProc();

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "refs/tags/v1.0")], Token);

        Assert.Empty(states);
        Assert.Empty(proc.Requests);
    }
}
