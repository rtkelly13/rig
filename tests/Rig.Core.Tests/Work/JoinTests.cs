using Rig.Core.Text;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

public sealed class JoinTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private const string Root = "/code";

    private static readonly string RepoPath = Path.Join(Root, "acme", "app");

    private static readonly string Worktree = Path.Join(RepoPath, "temp", "wt-feature");

    [Theory]
    [InlineData("feat/PROJ-1234-thing", "PROJ-1234")]
    [InlineData("proj-3583-hub-fixes", "PROJ-3583")]
    [InlineData("rr-802/feed", "RR-802")]
    [InlineData("docs/proj-12-x-PROJ-99", "PROJ-99")]
    [InlineData("pr-894-review", null)]
    [InlineData("clone-959", null)]
    [InlineData("fix-123", null)]
    [InlineData("v-10", null)]
    [InlineData("feat/work-items", null)]
    [InlineData("snapshot-2026", null)]
    [InlineData("main", null)]
    [InlineData(null, null)]
    public void ParseTicket_matches_the_recorded_cases(string? branch, string? ticket) =>
        Assert.Equal(ticket, WorkJoin.ParseTicket(branch));

    [Theory]
    [InlineData("busy", true, true, true, "answered", WorkSessionState.Live)]
    [InlineData("idle", true, false, false, "answered", WorkSessionState.WaitingOnUser)]
    [InlineData(null, false, true, true, "answered", WorkSessionState.Cleared)]
    [InlineData(null, false, false, true, "mid-tool", WorkSessionState.EndedClean)]
    [InlineData(null, false, false, false, "mid-tool", WorkSessionState.DiedMidTask)]
    [InlineData(null, false, false, false, "unanswered", WorkSessionState.DiedMidTask)]
    [InlineData(null, false, false, false, "answered", WorkSessionState.DiedIdle)]
    public void ClassifySession_matches_the_recorded_cases(
        string? liveStatus,
        bool isLive,
        bool cleared,
        bool exited,
        string turn,
        WorkSessionState expected) =>
        Assert.Equal(expected, WorkJoin.ClassifySession(liveStatus, isLive, cleared, exited, turn));

    [Fact]
    public void Attribute_prefers_the_worktree_nested_inside_its_clone()
    {
        var index = new CheckoutIndex([Repo()]);

        var got = WorkJoin.Attribute(Session(Path.Join(Worktree, "src")), index, _ => true, _ => null);

        Assert.Equal(new Attribution(RepoPath, "feat/PROJ-1-thing"), got);
    }

    [Fact]
    public void Attribute_the_clone_root_is_its_main_checkout()
    {
        var index = new CheckoutIndex([Repo()]);

        var got = WorkJoin.Attribute(Session(RepoPath), index, _ => true, _ => null);

        Assert.Equal(new Attribution(RepoPath, "main"), got);
    }

    [Fact]
    public void Attribute_a_removed_directory_goes_unattached()
    {
        var index = new CheckoutIndex([Repo()]);

        var got = WorkJoin.Attribute(Session(Worktree), index, _ => false, _ => null);

        Assert.Equal(new Attribution(null, Reason: $"directory gone: {Worktree}"), got);
    }

    [Fact]
    public void Attribute_falls_back_to_the_resolver_then_to_unattached()
    {
        var index = new CheckoutIndex([Repo()]);
        var elsewhere = new Attribution("/other", "dev");

        var resolved = WorkJoin.Attribute(Session("/elsewhere"), index, _ => true, _ => elsewhere);
        var outside = WorkJoin.Attribute(Session(Root), index, _ => true, _ => null);

        Assert.Equal(elsewhere, resolved);
        Assert.Equal(new Attribution(null, Reason: $"not in a git checkout: {Root}"), outside);
    }

    [Fact]
    public void Attribute_uses_start_cwd_when_last_is_missing()
    {
        var index = new CheckoutIndex([Repo()]);
        var session = new SessionFacts("s", "t", WorkSessionState.DiedIdle, null, null, RepoPath, 1);

        var got = WorkJoin.Attribute(session, index, _ => true, _ => null);

        Assert.Equal(new Attribution(RepoPath, "main"), got);
    }

    [Fact]
    public void Attribute_without_a_working_directory_is_unattached()
    {
        var index = new CheckoutIndex([Repo()]);
        var session = new SessionFacts("s", "t", WorkSessionState.DiedIdle, null, null, null, 1);

        Assert.Equal(
            new Attribution(null, Reason: "no working directory recorded"),
            WorkJoin.Attribute(session, index, _ => true, _ => null));
    }

    [Fact]
    public void Trunk_wins_over_every_other_signal()
    {
        var decision = Decide(isTrunk: true, pr: Pr(PrStatus.Merged), dirty: true);

        AssertState(WorkState.Trunk, ["default branch", "uncommitted changes"], decision);
    }

    [Fact]
    public void Merged_lists_only_the_cleanup_blockers()
    {
        var decision = Decide(
            pr: Pr(PrStatus.Merged),
            dirty: true,
            sessions: [Session(Worktree, WorkSessionState.WaitingOnUser)],
            branchRef: new BranchRef("b", Unpushed: 3));

        AssertState(WorkState.Merged, ["PR #7 merged", "1 running session", "uncommitted changes"], decision);
    }

    [Fact]
    public void An_open_or_draft_pr_or_unpushed_commits_are_active()
    {
        AssertState(WorkState.Active, ["draft PR #7"], Decide(pr: Pr(PrStatus.Draft)));
        AssertState(WorkState.Active, ["2 unpushed commits"], Decide(branchRef: new BranchRef("b", Unpushed: 2)));
    }

    [Fact]
    public void Closed_is_overridden_by_a_signal_that_keeps_it_active()
    {
        var gone = new BranchRef("b", Upstream: "origin/b", UpstreamGone: true);

        AssertState(WorkState.Closed, ["PR #7 closed unmerged", "upstream gone"], Decide(pr: Pr(PrStatus.Closed), branchRef: gone));
        Assert.Equal(WorkState.Active, Decide(pr: Pr(PrStatus.Closed), dirty: true).State);
    }

    [Fact]
    public void Recent_activity_is_active_and_older_activity_is_stale()
    {
        AssertState(WorkState.Active, ["activity 2d ago"], Decide(lastActivity: Now - TimeSpan.FromDays(2)));
        AssertState(WorkState.Active, ["activity 5h ago"], Decide(lastActivity: Now - TimeSpan.FromHours(5)));
        AssertState(WorkState.Active, ["activity 0m ago"], Decide(lastActivity: Now - TimeSpan.FromSeconds(30)));
        AssertState(WorkState.Stale, ["no activity for 30d"], Decide(lastActivity: Now - TimeSpan.FromDays(30)));
        AssertState(WorkState.Stale, ["no activity recorded"], Decide(noActivity: true));
    }

    [Fact]
    public void BuildItems_joins_sessions_prs_and_the_unattached_bucket()
    {
        var items = WorkJoin.BuildItems(WorkFixtures.Inputs(), Now, WorkJoin.DefaultStaleAfter);

        Assert.Equal(
            [
                ("acme/app:feat/PROJ-3-open", WorkState.Active, PrLookup.Ok),
                ("acme/app:(detached)", WorkState.Active, PrLookup.Skipped),
                ("other/lib:feat/PROJ-6-open", WorkState.Active, PrLookup.Ok),
                ("acme/app:feat/PROJ-1-thing", WorkState.Merged, PrLookup.Ok),
                ("other/lib:release/PROJ-2", WorkState.Closed, PrLookup.Ok),
                ("acme/app:feat/PROJ-5-stale", WorkState.Stale, PrLookup.Ok),
                ("acme/app:main", WorkState.Trunk, PrLookup.Skipped),
                ("other/lib:master", WorkState.Trunk, PrLookup.Skipped),
                ("unattached", WorkState.Unattached, PrLookup.Skipped),
            ],
            items.Select(item => (item.Key, item.State, item.PrLookup)));

        var feature = items.Single(item => item.Key == "acme/app:feat/PROJ-1-thing");
        Assert.Equal("PROJ-1", feature.Ticket);
        Assert.Equal(2, feature.Sessions.Count);
        Assert.Equal(Now - TimeSpan.FromMinutes(2), feature.LastActivity);
        Assert.True(feature.Dirty);
        Assert.Equal(4, feature.Unpushed);
        Assert.Single(feature.RunningSessions);
        Assert.Single(feature.Checkouts);

        var unattached = items[^1];
        Assert.Null(unattached.Repo);
        Assert.Null(unattached.RepoPath);
        Assert.Null(unattached.Branch);
        Assert.Single(unattached.Sessions);
    }

    [Fact]
    public void A_session_branch_without_a_checkout_becomes_an_item()
    {
        var items = WorkJoin.BuildItems(
            new JoinInputs(
                [Repo()],
                [new AttributedSession(Session(Worktree), new Attribution(RepoPath, "gone-branch"))],
                new Dictionary<BranchKey, PrInfo>(),
                new Dictionary<string, PrLookup> { [RepoPath] = PrLookup.Ok }),
            Now,
            WorkJoin.DefaultStaleAfter);

        Assert.Contains(items, item => item.Key == "acme/app:gone-branch");
    }

    [Fact]
    public void Filter_hides_quiet_trunk_unless_asked()
    {
        var items = Build([]);
        Assert.Equal(2, items.Count);

        Assert.Equal(["acme/app:feat/PROJ-1-thing"], WorkJoin.FilterItems(items, new ItemFilter()).Select(item => item.Key));
        Assert.Equal(2, WorkJoin.FilterItems(items, new ItemFilter { IncludeQuietTrunk = true }).Count);
        Assert.Equal(
            ["acme/app:main"],
            WorkJoin.FilterItems(items, new ItemFilter { States = new HashSet<WorkState> { WorkState.Trunk } }).Select(item => item.Key));
        Assert.Empty(WorkJoin.FilterItems(items, new ItemFilter { Repo = "nope" }));
        Assert.Empty(WorkJoin.FilterItems(items, new ItemFilter { Since = Now - TimeSpan.FromDays(1), IncludeQuietTrunk = true }));
        Assert.Single(WorkJoin.FilterItems(items, new ItemFilter { Since = Now - TimeSpan.FromDays(3), IncludeQuietTrunk = true }));
    }

    [Fact]
    public void A_session_in_an_outside_repo_gets_a_minimal_view()
    {
        var outside = "/outside/app";
        var items = WorkJoin.BuildItems(
            new JoinInputs(
                [],
                [new AttributedSession(Session(outside), new Attribution(outside, "dev"))],
                new Dictionary<BranchKey, PrInfo>(),
                new Dictionary<string, PrLookup>()),
            Now,
            WorkJoin.DefaultStaleAfter);

        var item = Assert.Single(items);
        Assert.Equal("app:dev", item.Key);
        Assert.Equal("app", item.Repo);
        Assert.Equal(outside, item.RepoPath);
        Assert.Empty(item.Checkouts);
        Assert.Equal(WorkState.Active, item.State);
    }

    [Fact]
    public void A_repo_without_an_origin_head_falls_back_to_main_or_master()
    {
        var repo = new RepoFacts(
            RepoPath,
            "acme/app",
            string.Empty,
            [
                new Checkout(RepoPath, "main", IsMainCheckout: true, Dirty: false),
                new Checkout(Worktree, "feature", IsMainCheckout: false, Dirty: false),
            ]);
        var items = WorkJoin.BuildItems(
            new JoinInputs([repo], [], new Dictionary<BranchKey, PrInfo>(), new Dictionary<string, PrLookup>()),
            Now,
            WorkJoin.DefaultStaleAfter);

        Assert.True(items.Single(item => item.Branch == "main").IsTrunk);
        Assert.False(items.Single(item => item.Branch == "feature").IsTrunk);
        Assert.Empty(items.Single(item => item.Branch == "main").RunningSessions);
        Assert.Equal(0, items.Single(item => item.Branch == "main").Unpushed);
    }

    [Fact]
    public void Filter_never_matches_the_unattached_bucket_by_repo()
    {
        var items = WorkJoin.BuildItems(WorkFixtures.Inputs(), Now, WorkJoin.DefaultStaleAfter);

        Assert.DoesNotContain(
            WorkJoin.FilterItems(items, new ItemFilter { Repo = "acme", IncludeQuietTrunk = true }),
            item => item.Key == WorkJoin.UnattachedKey);
    }

    [Fact]
    public void CountStates_lists_every_state_in_display_order()
    {
        var counts = WorkJoin.CountStates(WorkJoin.BuildItems(WorkFixtures.Inputs(), Now, WorkJoin.DefaultStaleAfter));

        Assert.Equal(
            [WorkState.Active, WorkState.Merged, WorkState.Closed, WorkState.Stale, WorkState.Trunk, WorkState.Unattached],
            counts.Keys);
        Assert.Equal(3, counts[WorkState.Active]);
        Assert.Equal(1, counts[WorkState.Merged]);
        Assert.Equal(1, counts[WorkState.Closed]);
        Assert.Equal(1, counts[WorkState.Stale]);
        Assert.Equal(2, counts[WorkState.Trunk]);
        Assert.Equal(1, counts[WorkState.Unattached]);
    }

    [Fact]
    public void IsQuietTrunk_requires_trunk_with_no_sessions_and_no_dirty_checkout()
    {
        Assert.True(WorkJoin.IsQuietTrunk(Item(WorkState.Trunk, [], dirty: false)));
        Assert.False(WorkJoin.IsQuietTrunk(Item(WorkState.Trunk, [], dirty: true)));
        Assert.False(WorkJoin.IsQuietTrunk(Item(WorkState.Trunk, [Session(Worktree)], dirty: false)));
        Assert.False(WorkJoin.IsQuietTrunk(Item(WorkState.Active, [], dirty: false)));
    }

    private static RepoFacts Repo(params BranchRef[] branches) => new(
        RepoPath,
        "acme/app",
        "main",
        [
            new Checkout(RepoPath, "main", IsMainCheckout: true, Dirty: false),
            new Checkout(Worktree, "feat/PROJ-1-thing", IsMainCheckout: false, Dirty: false),
        ])
    {
        Branches = branches,
    };

    private static SessionFacts Session(string? lastCwd, WorkSessionState state = WorkSessionState.EndedClean) =>
        new("s1", "t", state, Now - TimeSpan.FromHours(1), lastCwd, lastCwd, 1);

    private static PrInfo Pr(PrStatus status) => new(7, status, "u");

    private static void AssertState(WorkState state, string[] reasons, StateDecision decision)
    {
        Assert.Equal(state, decision.State);
        Assert.Equal(reasons, decision.Reasons);
    }

    private static StateDecision Decide(
        bool isTrunk = false,
        PrInfo? pr = null,
        bool dirty = false,
        IReadOnlyList<SessionFacts>? sessions = null,
        BranchRef? branchRef = null,
        DateTimeOffset? lastActivity = null,
        bool noActivity = false) =>
        WorkJoin.DeriveState(
            isTrunk,
            pr,
            [new Checkout(Worktree, "b", IsMainCheckout: false, Dirty: dirty)],
            sessions ?? [],
            branchRef,
            noActivity ? null : lastActivity ?? Now - TimeSpan.FromDays(30),
            Now,
            WorkJoin.DefaultStaleAfter);

    private static IReadOnlyList<WorkItem> Build(IReadOnlyList<AttributedSession> sessions)
    {
        var feature = new BranchKey(RepoPath, "feat/PROJ-1-thing");
        return WorkJoin.BuildItems(
            new JoinInputs(
                [Repo(branches: [new BranchRef("feat/PROJ-1-thing", Now - TimeSpan.FromDays(40))])],
                sessions,
                new Dictionary<BranchKey, PrInfo>
                {
                    [feature] = new PrInfo(9, PrStatus.Merged, "u", Now - TimeSpan.FromDays(3)),
                },
                new Dictionary<string, PrLookup> { [RepoPath] = PrLookup.Ok }),
            Now,
            WorkJoin.DefaultStaleAfter);
    }

    private static WorkItem Item(WorkState state, IReadOnlyList<SessionFacts> sessions, bool dirty) => new(
        "key",
        "repo",
        RepoPath,
        "branch",
        null,
        state == WorkState.Trunk,
        state,
        [],
        [new Checkout(Worktree, "branch", IsMainCheckout: false, Dirty: dirty)],
        null,
        null,
        PrLookup.Skipped,
        sessions,
        null);
}
