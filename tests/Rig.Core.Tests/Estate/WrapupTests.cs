using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using static Rig.Core.Tests.Estate.WrapupFixtures;

namespace Rig.Core.Tests.Estate;

/// <summary><c>estate wrapup</c>'s planning: scope, targets, statuses, the prompt and the sends. Ported from <c>tests/test_estate_wrapup.py</c>.</summary>
public sealed class WrapupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WrapupTarget Target(string[]? repos = null, bool all = false, string[]? worktrees = null, bool children = false, WrapupScope scope = WrapupScope.Personal) =>
        new(scope, repos ?? [], all, worktrees ?? [], children);

    private static string[] Ids(IReadOnlyList<WrapupPlaced> placed) => [.. placed.Select(p => p.Worktree.WorktreeId)];

    [Fact]
    public void Classify_puts_the_personal_and_org_folders_apart_from_work_and_elsewhere()
    {
        Assert.Equal(WrapupScope.Personal, Wrapup.Classify(Under("personal", "rig"), Personal, Work, Home));
        Assert.Equal(WrapupScope.Personal, Wrapup.Classify(Under("rtkelly-labs", "design-system", "temp", "x"), Personal, Work, Home));
        Assert.Equal(WrapupScope.Work, Wrapup.Classify(Under("acme", "platform"), Personal, Work, Home));
        Assert.Null(Wrapup.Classify(Under("other", "dotnet"), Personal, Work, Home));
        Assert.Equal(WrapupScope.Personal, Wrapup.Classify(Path.Combine("~", "rig"), Personal, Work, Under("personal")));
    }

    [Fact]
    public void Select_scopes_a_worktree_by_its_repos_folder_not_its_own()
    {
        var elsewhere = Worktree("w1", path: Under("elsewhere", "w1"));
        var work = Worktree("w2", repo: "platform", repoId: "work-id");

        var placed = Wrapup.Select(Snapshot(elsewhere, work), Target(all: true), Personal, Work, Home, Root);

        Assert.Equal(["w1"], Ids(placed));
    }

    [Fact]
    public void Select_matches_a_repo_without_regard_to_case_and_a_worktree_by_branch_or_path()
    {
        var a = Worktree("alpha");
        var b = Worktree("beta");
        var snapshot = Snapshot(a, b);

        Assert.Equal(["alpha", "beta"], Ids(Wrapup.Select(snapshot, Target(repos: ["RIG"]), Personal, Work, Home, Root)));
        Assert.Equal(["beta"], Ids(Wrapup.Select(snapshot, Target(worktrees: ["rtkelly13/beta"]), Personal, Work, Home, Root)));
        Assert.Equal(["alpha"], Ids(Wrapup.Select(snapshot, Target(worktrees: [a.Path]), Personal, Work, Home, Root)));
        Assert.Equal(["alpha"], Ids(Wrapup.Select(snapshot, Target(repos: ["rig"], worktrees: ["ALPHA"]), Personal, Work, Home, Root)));
        Assert.Empty(Wrapup.Select(snapshot, Target(repos: ["design-system"], worktrees: ["alpha"]), Personal, Work, Home, Root));
    }

    [Fact]
    public void Select_follows_children_in_tree_order_and_never_across_scopes()
    {
        var parent = Worktree("p");
        var second = Worktree("z-child", parent: "p");
        var first = Worktree("a-child", parent: "p");
        var grandchild = Worktree("g", parent: "a-child");
        var workChild = Worktree("work-child", repo: "platform", repoId: "work-id", parent: "p");
        var snapshot = Snapshot(second, grandchild, parent, first, workChild);

        var placed = Wrapup.Select(snapshot, Target(worktrees: ["p"], children: true), Personal, Work, Home, Root);
        var alone = Wrapup.Select(snapshot, Target(worktrees: ["p"]), Personal, Work, Home, Root);

        Assert.Equal(["p", "a-child", "g", "z-child"], Ids(placed));
        Assert.Equal([0, 1, 2, 1], placed.Select(p => p.Depth));
        Assert.Equal(["p"], Ids(alone));
    }

    [Theory]
    [InlineData(false, 0, true, null, true)]
    [InlineData(true, 0, true, null, false)]
    [InlineData(false, 1, true, null, false)]
    [InlineData(false, 0, false, null, false)]
    [InlineData(false, 0, false, "merged", true)]
    [InlineData(false, 0, true, "open", false)]
    [InlineData(false, 0, false, "closed", true)]
    public void IsSettled_needs_clean_pushed_and_merged(bool dirty, int unpushed, bool merged, string? prState, bool settled)
    {
        var pr = prState is null ? null : new WrapupPr(7, prState);

        Assert.Equal(settled, Wrapup.IsSettled(new GitFacts(dirty, unpushed, merged), pr));
        Assert.False(Wrapup.IsSettled(null, pr));
    }

    [Fact]
    public void DescribeFacts_reads_as_one_line()
    {
        Assert.Equal("folder missing", Wrapup.DescribeFacts(null, null));
        Assert.Equal("clean; no PR", Wrapup.DescribeFacts(Clean, null));
        Assert.Equal("uncommitted changes; 2 commit(s) on no remote; nothing beyond the default branch", Wrapup.DescribeFacts(new GitFacts(true, 2, true), null));
        Assert.Equal("clean; PR #7 open, checks failure", Wrapup.DescribeFacts(Clean, new WrapupPr(7, "open", "failure")));
        Assert.Equal("clean; PR #7 merged", Wrapup.DescribeFacts(Clean, new WrapupPr(7, "merged", "success")));
        Assert.Equal("folder missing; PR #7 ?", Wrapup.DescribeFacts(null, new WrapupPr(7)));
    }

    [Fact]
    public async Task Plan_gives_each_kind_of_session_its_status()
    {
        WrapupWorktree[] worktrees =
        [
            Worktree("ask", agents: Agent("t1:l1")),
            Worktree("working", agents: Agent("t2:l2", state: "working")),
            Worktree("answering", agents: Agent("t3:l3", state: "working", prompt: $"{Wrapup.Marker} done?")),
            Worktree("waiting", agents: Agent("t4:l4", state: "waiting", waitingMessage: "Push to origin?")),
            Worktree("answered", agents: Agent("t5:l5", prompt: $"{Wrapup.Marker} done?", reply: "All good.\nWRAPUP: pending: merge #9")),
            Worktree("recent", agents: Agent("t6:l6", idle: 5)),
            Worktree("no-terminal", agents: Agent("t7:l7")),
            Worktree("shell", agents: Agent("t8:l8")),
        ];
        WrapupTerminal[] terminals = [.. Enumerable.Range(1, 6).Select(n => Terminal($"t{n}:l{n}")), Terminal("t8:l8", identity: null)];

        var rows = await PlanAsync(worktrees, terminals);

        Assert.Equal(
            [WrapupStatus.Ask, WrapupStatus.Working, WrapupStatus.Answering, WrapupStatus.Waiting, WrapupStatus.Answered, WrapupStatus.Recent, WrapupStatus.NoTerminal, WrapupStatus.NoTerminal],
            rows.Select(r => r.Status));
        Assert.Equal("Push to origin?", rows[3].Note);
        Assert.Equal(new WrapupVerdict(false, "merge #9"), rows[4].Verdict);
        Assert.Equal("term_t1", rows[0].Terminal);
        Assert.Equal(120, rows[0].IdleMinutes);
        Assert.Equal("rtkelly13/ask", rows[0].Branch);
        Assert.Equal("session t1:l1", rows[0].Session);
    }

    [Fact]
    public async Task Plan_leaves_out_sub_agents_and_other_agent_kinds()
    {
        var w = Worktree("w", agents: [Agent("t1:l1"), Agent("t1:l2", parentPane: "t1:l1"), Agent("t1:l3", agentType: "codex")]);

        var rows = await PlanAsync([w], [Terminal("t1:l1")]);

        Assert.Equal(["t1:l1"], rows.Select(r => r.Pane));
    }

    [Fact]
    public async Task Plan_settles_finished_work_unless_asked_to_include_it()
    {
        var w = Worktree("w", agents: Agent("t1:l1"));
        var merged = new GitFacts(false, 0, true);

        var settled = await PlanAsync([w], [Terminal("t1:l1")], merged);
        var included = await PlanAsync([w], [Terminal("t1:l1")], merged, new WrapupOptions(Wrapup.DefaultIdle, null, IncludeSettled: true));

        Assert.Equal(WrapupStatus.Settled, Assert.Single(settled).Status);
        Assert.Equal(WrapupStatus.Ask, Assert.Single(included).Status);
    }

    [Fact]
    public async Task Max_defers_the_parent_so_its_subtasks_go_first()
    {
        WrapupWorktree[] worktrees = [Worktree("p", agents: Agent("t1:l1")), Worktree("c", parent: "p", agents: Agent("t2:l2"))];

        var rows = await PlanAsync(worktrees, [Terminal("t1:l1"), Terminal("t2:l2")], options: new WrapupOptions(Wrapup.DefaultIdle, 1));

        Assert.Equal([WrapupStatus.Deferred, WrapupStatus.Ask], rows.Select(r => r.Status));
        Assert.Equal(["c"], rows[0].Children);
        Assert.Equal([1, 0], Wrapup.SendOrder([rows[0] with { Status = WrapupStatus.Ask }, rows[1]]));
    }

    [Fact]
    public async Task An_answer_without_a_wrapup_line_says_what_it_did_say()
    {
        var w = Worktree("w", agents: Agent("t1:l1", prompt: $"  {Wrapup.Marker} done?", reply: "\nI merged it."));

        var row = Assert.Single(await PlanAsync([w], [Terminal("t1:l1")]));

        Assert.Equal(WrapupStatus.Answered, row.Status);
        Assert.Null(row.Verdict);
        Assert.Equal("replied without a WRAPUP line: I merged it.", row.Note);
    }

    [Theory]
    [InlineData("WRAPUP: done", true, "")]
    [InlineData("Summary.\n**WRAPUP: pending: merge #9**", false, "merge #9")]
    [InlineData("- `wrapup — PENDING — rebase onto main`", false, "rebase onto main")]
    [InlineData("WRAPUP: Done. All merged.", true, "All merged.")]
    public void ParseVerdict_reads_the_first_wrapup_line(string message, bool done, string detail) =>
        Assert.Equal(new WrapupVerdict(done, detail), Wrapup.ParseVerdict(message));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("All done.")]
    [InlineData("the WRAPUP: done line is next")]
    public void ParseVerdict_finds_nothing_without_a_wrapup_line(string? message) => Assert.Null(Wrapup.ParseVerdict(message));

    [Fact]
    public async Task OpenChildren_counts_descendants_not_yet_finished()
    {
        WrapupWorktree[] worktrees =
        [
            Worktree("p", agents: Agent("t1:l1")),
            Worktree("done", parent: "p", agents: Agent("t2:l2", prompt: $"{Wrapup.Marker} ?", reply: "WRAPUP: done")),
            Worktree("open", parent: "p", agents: Agent("t3:l3")),
            Worktree("deep", parent: "open", agents: Agent("t4:l4", state: "working")),
        ];

        var rows = await PlanAsync(worktrees, [Terminal("t1:l1"), Terminal("t3:l3")]);

        Assert.Equal(2, Wrapup.OpenChildren(rows[0], rows));
        Assert.Equal(0, Wrapup.OpenChildren(rows[1], rows));
    }

    [Fact]
    public async Task RenderPrompt_fills_the_template_and_summarises_the_children()
    {
        var parent = Worktree("p", agents: Agent("t1:l1"));
        var child = Worktree("c", parent: "p", pr: new WrapupPr(4, "open", "success"), agents: Agent("t2:l2", prompt: $"{Wrapup.Marker} ?", reply: "WRAPUP: pending: land #4"));
        var quiet = Worktree("q", parent: "p");
        var rows = await PlanAsync([parent, child, quiet], [Terminal("t1:l1")]);
        var worktrees = new Dictionary<string, WrapupWorktree> { ["p"] = parent, ["c"] = child, ["q"] = quiet };

        var prompt = Wrapup.RenderPrompt(
            "\n$repo/$worktree on $branch: $facts. Children: $children. $$5 ${report} $unknown $\n", rows[0] with { AskId = "cafe0001" }, rows, worktrees, "rig");

        Assert.Equal(
            "[Wrap-up check] rig/p on rtkelly13/p: clean; no PR. Children: rig/c (rtkelly13/c; clean; PR #4 open, checks success; pending: land #4); rig/q (rtkelly13/q; folder missing; no agent). "
            + "$5 rig estate wrapup report --ask cafe0001 $unknown $",
            prompt);
        Assert.Contains("Children: none.", Wrapup.RenderPrompt("Children: $children.", rows[1], rows, worktrees, "rig"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_shipped_template_names_every_placeholder_it_uses()
    {
        var template = File.ReadAllText(Path.Join(Path.GetDirectoryName(RepoTemp.Root), "config", Wrapup.TemplateFile));
        var row = new WrapupRow("rig", "w", "w", "/w", "", 0, "s", "done", WrapupStatus.Ask) { AskId = "cafe0001" };

        var prompt = Wrapup.RenderPrompt(template, row, [row], new Dictionary<string, WrapupWorktree>(), "rig");

        Assert.StartsWith(Wrapup.Marker, prompt, StringComparison.Ordinal);
        Assert.Contains("--ask cafe0001", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("$repo", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("$report", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("e5e7d88c", "/Users/me/Library/Application Support/rig/bin/rig", "'/Users/me/Library/Application Support/rig/bin/rig' estate wrapup report --ask e5e7d88c")]
    [InlineData("", "rig", "rig estate wrapup report")]
    [InlineData("a", "it's", "'it'\"'\"'s' estate wrapup report --ask a")]
    public void ReportCommand_is_shell_quoted(string askId, string rig, string expected) => Assert.Equal(expected, Wrapup.ReportCommand(askId, rig));

    [Fact]
    public async Task ApplySends_asks_the_deepest_first_with_orcas_send_flags()
    {
        WrapupWorktree[] worktrees = [Worktree("p", agents: Agent("t1:l1")), Worktree("c", parent: "p", agents: Agent("t2:l2"))];
        var rows = await PlanAsync(worktrees, [Terminal("t1:l1"), Terminal("t2:l2")]);
        var proc = new FakeProc();
        proc.Answer(new Ran(0, """{"ok": true, "result": {"submitted": true}}""", ""), "orca", "terminal", "send");
        var ids = 0;

        var sent = await WrapupRun.ApplySendsAsync(rows, "$worktree $report", worktrees.ToDictionary(w => w.WorktreeId), new WrapupOrca(proc), () => $"id{++ids}", "rig", Token);

        Assert.Equal(["term_t2", "term_t1"], proc.Requests.Select(r => r.Argv[4]));
        Assert.Equal(["--text", "[Wrap-up check] c rig estate wrapup report --ask id1", "--enter", "--wait-submit", "10", "--json"], proc.Requests[0].Argv.Skip(5));
        Assert.Equal([WrapupStatus.Sent, WrapupStatus.Sent], sent.Select(r => r.Status));
        Assert.Equal(["id2", "id1"], sent.Select(r => r.AskId));
    }

    [Theory]
    [InlineData(1, """{"ok": false, "error": {"message": "pane gone"}}""", "", "pane gone")]
    [InlineData(0, """{"ok": false}""", "", "{\"ok\": false}")]
    [InlineData(2, "", "boom\nmore", "boom")]
    [InlineData(4, "", "", "exit 4")]
    public async Task A_refused_send_says_why(int code, string output, string error, string note)
    {
        var rows = await PlanAsync([Worktree("w", agents: Agent("t1:l1"))], [Terminal("t1:l1")]);
        var proc = new FakeProc();
        proc.Answer(new Ran(code, output, error), "orca", "terminal", "send");

        var sent = Assert.Single(await WrapupRun.ApplySendsAsync(rows, "x", new Dictionary<string, WrapupWorktree>(), new WrapupOrca(proc), () => "id", "rig", Token));

        Assert.Equal(WrapupStatus.SendFailed, sent.Status);
        Assert.Equal(note, sent.Note);
    }

    [Fact]
    public async Task ReadSnapshot_decodes_orca_and_drops_rows_it_cannot_read()
    {
        var proc = new FakeProc();
        proc.Answer(
            new Ran(0, """
                {"ok": true, "result": {"truncated": true, "worktrees": [
                  {"worktreeId": "w1", "repoId": "r", "path": "/w1", "branch": "refs/heads/x", "parentWorktreeId": "w0",
                   "linkedPR": {"number": 7, "state": "open"},
                   "agents": [{"paneKey": "t1:l1", "state": "done", "agentType": "claude", "stateStartedAt": 1.5,
                               "programStatus": {"message": "hi"}}, {"state": "done"}]},
                  {"repoId": "r", "path": "/nope"},
                  {"worktreeId": "w2", "repoId": "r", "path": "/w2", "linkedPR": {"number": "x"}, "agents": "no"}]}}
                """, ""),
            "orca", "worktree", "ps");
        proc.Answer(new Ran(0, """{"ok": true, "result": {"terminals": [{"handle": "h1", "tabId": "t1", "leafId": "l1", "writable": true}, {"tabId": "t2"}]}}""", ""), "orca", "terminal", "list");
        proc.Answer(new Ran(0, """{"ok": true, "result": {"repos": [{"id": "r", "path": "/r", "displayName": "R"}]}}""", ""), "orca", "repo", "list");

        var snapshot = await new WrapupOrca(proc).ReadSnapshotAsync(Token);

        Assert.True(snapshot.Truncated);
        Assert.Equal(["w1", "w2"], snapshot.Worktrees.Select(w => w.WorktreeId));
        var w1 = snapshot.Worktrees[0];
        Assert.Equal(("x", "w0", 7L), (w1.BranchName, w1.ParentId, w1.LinkedPr!.Number));
        var agent = Assert.Single(w1.Agents!);
        Assert.Equal((1.5, "hi"), (agent.StateStartedAt, agent.ProgramStatus!.Message));
        Assert.Null(snapshot.Worktrees[1].LinkedPr);
        Assert.Null(snapshot.Worktrees[1].Agents);
        Assert.Equal("t1:l1", Assert.Single(snapshot.Terminals).PaneKey);
        Assert.Equal(new WrapupRepo("r", "/r", "R"), Assert.Single(snapshot.Repos));
        Assert.Equal(["orca", "worktree", "ps", "--limit", "1000", "--json"], proc.Requests[0].Argv);
    }

    [Fact]
    public async Task Orca_down_or_unreadable_is_an_error()
    {
        var down = new FakeProc();
        down.Answer(new Ran(1, "", "\norca is not running\nretry"), "orca");
        var garbled = new FakeProc();
        garbled.Answer(new Ran(0, "not json", ""), "orca");

        var stopped = await Assert.ThrowsAsync<WrapupException>(() => new WrapupOrca(down).ReadSnapshotAsync(Token));
        var unreadable = await Assert.ThrowsAsync<WrapupException>(() => new WrapupOrca(garbled).ReadWorktreesAsync(Token));

        Assert.Equal("orca worktree ps failed: orca is not running", stopped.Message);
        Assert.Contains("shape", unreadable.Message, StringComparison.Ordinal);
    }
}
