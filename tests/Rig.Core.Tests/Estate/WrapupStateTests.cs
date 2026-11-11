using System.Text;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Otel;
using static Rig.Core.Tests.Estate.WrapupFixtures;

namespace Rig.Core.Tests.Estate;

/// <summary>What <c>estate wrapup</c> remembers per worktree, and <c>estate wrapup report</c>. Ported from <c>tests/test_estate_wrapup_report.py</c>.</summary>
public sealed class WrapupStateTests
{
    /// <summary>Python's <c>encode_json</c> for <see cref="GoldenState"/>, byte for byte; the triage inbox still reads these files.</summary>
    private const string Golden =
        """{"schema":"rig.wrapup/1","worktree_id":"w—1","branch":"feat/é","asks":[{"at":"2026-11-07T11:00:00.000+00:00","pane":"t1:l1","session":"Fix the “thing” — é","head":"abc","facts":"clean; no PR","template_sha":"0123456789ab","id":"aaaa1111"}],"answers":[{"at":"2026-11-07T12:00:00.000+00:00","pane":"t1:l1","verdict":"pending","detail":"merge #9 <now> & \"then\"","head":"f00d","asked_at":"2026-11-07T11:00:00.000+00:00","todo":["merge #9","close #4"],"via":"report"},{"at":"x","pane":"t2:l2","verdict":"done","detail":"","head":"","asked_at":null,"todo":[],"via":"reply"}]}""";

    private static readonly WrapupState GoldenState = WrapupState.Empty("w—1", "feat/é")
        .WithAsk(new AskRecord("2026-11-07T11:00:00.000+00:00", "t1:l1", "Fix the “thing” — é", "abc", "clean; no PR", "0123456789ab", "aaaa1111"))
        .WithAnswer(new AnswerRecord("2026-11-07T12:00:00.000+00:00", "t1:l1", "pending", "merge #9 <now> & \"then\"", "f00d", "2026-11-07T11:00:00.000+00:00", ["merge #9", "close #4"], "report"))
        .WithAnswer(new AnswerRecord("x", "t2:l2", "done", "", ""));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<WrapupReported> ReportAsync(MemoryWrapupStore store, string askId = "", bool done = true, string pending = "", string[]? todo = null) =>
        WrapupRun.ReportAsync("/w", askId, done, pending, todo ?? [], Now, () => Task.FromResult("cafe"), store, Token);

    private static MemoryWrapupStore Asked(params AskRecord[] asks)
    {
        var store = new MemoryWrapupStore();
        store.Files["/w"] = asks.Aggregate(WrapupState.Empty("w", "b"), (s, a) => s.WithAsk(a));
        return store;
    }

    [Fact]
    public void The_state_file_encodes_byte_for_byte_as_python_did()
    {
        Assert.Equal(Golden, GoldenState.Encode());
        Assert.Equal(GoldenState.Encode(), WrapupState.Decode(Golden)!.Encode());
        Assert.Equal(["merge #9", "close #4"], WrapupState.Decode(Golden)!.Answers[0].Todo);
        Assert.Null(WrapupState.Decode("not json"));
    }

    [Fact]
    public async Task The_file_store_round_trips_and_ignores_another_schema()
    {
        using var root = new TempDir();
        var repo = await Repos.PublishedAsync(root);
        var store = new FileWrapupStore(new Git(Repos.Proc, root.Path));

        Assert.Null(await store.WriteAsync(repo, GoldenState, Token));
        var path = WorktreeState.StateFile(repo, WrapupState.Feature);

        Assert.Equal(Golden + "\n", await File.ReadAllTextAsync(path, Encoding.UTF8, Token));
        Assert.Equal(Golden, store.Read(repo)!.Encode());
        await File.WriteAllTextAsync(path, Golden.Replace("rig.wrapup/1", "rig.wrapup/2", StringComparison.Ordinal), Token);
        Assert.Null(store.Read(repo));
        Assert.Null(store.Read(root.Folder("nowhere")));
    }

    [Fact]
    public async Task The_file_store_refuses_a_folder_git_does_not_ignore()
    {
        using var root = new TempDir();
        var repo = await Repos.InitAsync(root.Folder("r"));

        var failure = await new FileWrapupStore(new Git(Repos.Proc, root.Path)).WriteAsync(repo, GoldenState, Token);

        Assert.NotNull(failure);
        Assert.False(File.Exists(WorktreeState.StateFile(repo, WrapupState.Feature)));
    }

    [Fact]
    public async Task An_ask_the_pane_never_moved_after_is_unconfirmed()
    {
        var w = Worktree("w", agents: Agent("t1:l1", idle: 120));
        var store = StoreWith(w, WrapupState.Empty().WithAsk(Ask(60)));

        var row = Assert.Single(await PlanAsync([w], [Terminal("t1:l1")], store: store));

        Assert.Equal(WrapupStatus.Unconfirmed, row.Status);
        Assert.Equal("asked 2026-11-07T11:00Z, but the pane has not moved since: check it", row.Note);
    }

    [Fact]
    public async Task A_session_resumed_since_its_answer_shows_the_last_answer()
    {
        var w = Worktree("w", agents: Agent("t1:l1", idle: 40));
        var ask = Ask(90);
        var store = StoreWith(w, WrapupState.Empty().WithAsk(ask).WithAnswer(new AnswerRecord("2026-11-07T10:45:00.000+00:00", "t1:l1", "pending", "merge #9", "abc", ask.At)));

        var row = Assert.Single(await PlanAsync([w], [Terminal("t1:l1")], store: store));

        Assert.Equal(WrapupStatus.Ask, row.Status);
        Assert.Equal("last answer 2026-11-07T10:45Z: pending: merge #9", row.Note);
    }

    [Fact]
    public async Task Record_writes_an_ask_once_and_a_reply_carries_the_asks_head()
    {
        var w = Worktree("w", agents: Agent("t1:l1"));
        var store = new MemoryWrapupStore();
        var row = Assert.Single(await PlanAsync([w], [Terminal("t1:l1")], new GitFacts(false, 0, false, "h1"), store: store)) with { Status = WrapupStatus.Sent, AskId = "id1" };

        _ = await WrapupRun.RecordAsync([row], Now, "tmpl", store, Token);
        var answered = Worktree("w", agents: Agent("t1:l1", idle: 1, prompt: $"{Wrapup.Marker} ?", reply: "WRAPUP: done"));
        var replied = await PlanAsync([answered], [Terminal("t1:l1")], new GitFacts(false, 0, false, "h2"), store: store);
        _ = await WrapupRun.RecordAsync(replied, Now, null, store, Token);
        _ = await WrapupRun.RecordAsync(await PlanAsync([answered], [Terminal("t1:l1")], store: store), Now, null, store, Token);

        var state = store.Files[w.Path];
        var ask = Assert.Single(state.Asks);
        Assert.Equal(("id1", "h1", "clean; no PR", "w", "rtkelly13/w"), (ask.Id, ask.Head, ask.Facts, state.WorktreeId, state.Branch));
        Assert.Equal(12, ask.TemplateSha.Length);
        var answer = Assert.Single(state.Answers);
        Assert.Equal(("done", "h1", ask.At, "reply"), (answer.Verdict, answer.Head, answer.AskedAt, answer.Via));
    }

    [Fact]
    public async Task A_refused_write_is_noted_on_every_row_of_the_worktree()
    {
        var w = Worktree("w", agents: [Agent("t1:l1"), Agent("t1:l2", state: "waiting", waitingMessage: "push?")]);
        var store = new MemoryWrapupStore { Refuse = "temp/ is not ignored" };
        var rows = await PlanAsync([w], [Terminal("t1:l1")], store: store);

        var recorded = await WrapupRun.RecordAsync([rows[0] with { Status = WrapupStatus.Sent }, rows[1]], Now, "t", store, Token);

        Assert.Equal(["state not recorded: temp/ is not ignored", "push?; state not recorded: temp/ is not ignored"], recorded.Select(r => r.Note));
    }

    [Fact]
    public async Task A_reply_is_not_recorded_over_the_agents_own_report()
    {
        var w = Worktree("w", agents: Agent("t1:l1", idle: 1, prompt: $"{Wrapup.Marker} ?", reply: "WRAPUP: done"));
        var ask = Ask(30, id: "a1");
        var reported = new AnswerRecord("r", "t1:l1", "pending", "merge #9", "f00d", ask.At, ["merge #9"], "report");
        var store = StoreWith(w, WrapupState.Empty().WithAsk(ask).WithAnswer(reported));

        var rows = await PlanAsync([w], [Terminal("t1:l1")], store: store);
        _ = await WrapupRun.RecordAsync(rows, Now, null, store, Token);

        var row = Assert.Single(rows);
        Assert.Equal((WrapupStatus.Answered, false, "merge #9"), (row.Status, row.Verdict!.Done, row.Verdict.Detail));
        Assert.Equal(reported, Assert.Single(store.Files[w.Path].Answers));
    }

    [Fact]
    public async Task A_reported_verdict_counts_as_answered_while_the_agent_finishes_and_is_never_unconfirmed()
    {
        var ask = Ask(30, id: "a1");
        var answer = new AnswerRecord("r", "t1:l1", "done", "", "f00d", ask.At, null, "report");
        var working = Worktree("w", agents: Agent("t1:l1", state: "working", prompt: $"{Wrapup.Marker} ?"));
        var still = Worktree("w", agents: Agent("t1:l1", idle: 120));

        var finishing = Assert.Single(await PlanAsync([working], [Terminal("t1:l1")], store: StoreWith(working, WrapupState.Empty().WithAsk(ask).WithAnswer(answer))));
        var quiet = Assert.Single(await PlanAsync([still], [Terminal("t1:l1")], store: StoreWith(still, WrapupState.Empty().WithAsk(ask).WithAnswer(answer))));

        Assert.Equal((WrapupStatus.Answered, true), (finishing.Status, finishing.Verdict!.Done));
        Assert.Equal(WrapupStatus.Ask, quiet.Status);
    }

    [Fact]
    public async Task Report_answers_the_named_ask()
    {
        var store = Asked(Ask(60, id: "a1"), Ask(30, pane: "t2:l2", id: "a2"));

        var reported = await ReportAsync(store, "a1", done: false, pending: "  merge #9 ", todo: ["merge #9"]);

        var answer = reported.Answer!;
        Assert.Equal(
            (WrapupStamps.Timestamp(Now), "t1:l1", "pending", "merge #9", "cafe", Ask(60).At, "report"),
            (answer.At, answer.Pane, answer.Verdict, answer.Detail, answer.Head, answer.AskedAt, answer.Via));
        Assert.Equal(["merge #9"], answer.Todo);
        Assert.Equal("", reported.Error);
        Assert.Equal(reported.Answer, Assert.Single(store.Files["/w"].Answers));
    }

    [Fact]
    public async Task Report_without_an_id_answers_the_newest_unanswered_ask_or_else_the_last()
    {
        var older = Ask(60, id: "a1");
        var newer = Ask(30, pane: "t2:l2", id: "a2");
        var store = Asked(older, newer);
        store.Files["/w"] = store.Files["/w"].WithAnswer(new AnswerRecord("x", "t2:l2", "done", "", "", newer.At));

        var first = await ReportAsync(store, done: false, todo: ["one", "two"]);
        var second = await ReportAsync(store);

        Assert.Equal(("t1:l1", older.At, "one; two"), (first.Answer!.Pane, first.Answer.AskedAt, first.Answer.Detail));
        Assert.Equal(("t2:l2", newer.At), (second.Answer!.Pane, second.Answer.AskedAt));
    }

    [Fact]
    public async Task Report_says_why_it_recorded_nothing()
    {
        var refusing = Asked(Ask(1, id: "a1"));
        refusing.Refuse = "temp/ is not ignored";

        Assert.Equal("no wrap-up ask is recorded in this worktree", (await ReportAsync(new MemoryWrapupStore())).Error);
        Assert.Equal("no ask zz is recorded in this worktree", (await ReportAsync(Asked(Ask(1, id: "a1")), "zz")).Error);
        var refused = await ReportAsync(refusing, "a1");
        Assert.Equal((null, "temp/ is not ignored"), (refused.Answer, refused.Error));
    }

    [Fact]
    public async Task The_follower_lock_lets_one_follower_ask_at_a_time()
    {
        using var root = new TempDir();
        var follower = new WrapupFollower(root.Path, new RecordingDetached(new FakeSignals()), ["rig"]);

        Assert.False(follower.Running());
        using (var held = follower.TryHold())
        {
            Assert.NotNull(held);
            Assert.Null(follower.TryHold());
            Assert.True(follower.Running());
        }

        Assert.False(follower.Running());
    }

    [Fact]
    public async Task The_follower_starts_rig_again_with_follow_and_a_dated_log()
    {
        using var root = new TempDir();
        var detached = new RecordingDetached(new FakeSignals());
        var follower = new WrapupFollower(root.Path, detached, ["dotnet", "rig.dll"]);

        var log = await follower.StartAsync(["--personal", "--apply"], Now, Token);

        Assert.Equal(Path.Join(root.Path, "logs", "wrapup", "follow-20261107T120000Z.log"), log);
        var start = Assert.Single(detached.Starts);
        Assert.Equal(["dotnet", "rig.dll", "estate", "wrapup", "--personal", "--apply", "--follow"], start.Argv);
        Assert.Equal(log, start.Log);
        Assert.True(Directory.Exists(follower.LogDir));
    }
}
