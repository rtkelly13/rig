using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Orca;

/// <summary><see cref="OrcaBaseRef"/>: reading each repo's local branch against its remote-tracking ref.</summary>
public sealed class OrcaBaseRefTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_branch_format_strips_the_ref_namespace()
    {
        Assert.Contains("%(refname:lstrip=2)", OrcaBaseRef.BranchFormat, StringComparison.Ordinal);
    }

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
    public void The_track_from_for_each_ref_answers_the_policy_ref()
    {
        const string output = "main\torigin/main\t[ahead 2, behind 3]\nfeature\torigin/feature\t\n";

        Assert.Equal(new LocalBaseState("main", "origin", 2, 3), OrcaBaseRef.FromTrack(output, "main", "origin"));
        Assert.Null(OrcaBaseRef.FromTrack(output, "main", "upstream"));
        Assert.Null(OrcaBaseRef.FromTrack(output, "absent", "origin"));
        Assert.Equal(("origin/feature", ""), OrcaBaseRef.FindBranch(output, "feature"));
    }

    [Fact]
    public async Task One_for_each_ref_call_answers_the_branch_and_its_counts()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(0, "main\torigin/main\t[ahead 2, behind 3]\n", string.Empty), "git", "for-each-ref");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        var state = Assert.Single(states);
        Assert.Equal("rig", state.Key);
        Assert.Equal(new LocalBaseState("main", "origin", 2, 3), state.Value);
        Assert.Single(proc.Requests);
        Assert.DoesNotContain(proc.Requests, request => request.Argv.Contains("rev-list"));
    }

    [Fact]
    public async Task A_repo_without_the_local_branch_is_skipped()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(0, "other\torigin/other\t\n", string.Empty), "git", "for-each-ref");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        Assert.Empty(states);
        Assert.Single(proc.Requests);
        Assert.DoesNotContain(proc.Requests, request => request.Argv.Contains("rev-list"));
    }

    [Fact]
    public async Task An_upstream_that_is_not_the_policy_ref_falls_back_to_rev_list()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(0, "main\tupstream/main\t[ahead 9, behind 9]\n", string.Empty), "git", "for-each-ref")
            .AnswerIn("/repos/rig", new Ran(0, "1\t2\n", string.Empty), "git", "rev-list", "--count", "--left-right", "refs/heads/main...refs/remotes/origin/main");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        Assert.Equal(new LocalBaseState("main", "origin", 1, 2), Assert.Single(states).Value);
        Assert.Equal(2, proc.Requests.Count);
        Assert.Contains(proc.Requests, request => request.Argv.Contains("rev-list"));
    }

    [Fact]
    public async Task A_repo_without_the_remote_tracking_ref_is_skipped()
    {
        var proc = new FakeProc()
            .AnswerIn("/repos/rig", new Ran(0, "main\t\t\n", string.Empty), "git", "for-each-ref")
            .AnswerIn("/repos/rig", new Ran(1, string.Empty, string.Empty), "git", "rev-list");

        var states = await OrcaBaseRef.InspectAsync(new Git(proc, "/code"), [("rig", "/repos/rig", "origin/main")], Token);

        Assert.Empty(states);
        Assert.Equal(2, proc.Requests.Count);
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
