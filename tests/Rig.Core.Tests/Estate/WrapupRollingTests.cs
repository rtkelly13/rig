using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;
using static Rig.Core.Tests.Estate.WrapupFixtures;
using View = System.Func<double, (string State, string Prompt, string? Reply)?>;

namespace Rig.Core.Tests.Estate;

/// <summary><c>estate wrapup</c>'s rolling send: a few asks at a time, the next as each session finishes. Ported from <c>tests/test_estate_wrapup_watch.py</c>.</summary>
public sealed class WrapupRollingTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(1);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(IReadOnlyList<WrapupRow> Rows, List<string> Said)> RunAsync(
        IReadOnlyList<WrapupRow> rows,
        IReadOnlyDictionary<string, WrapupWorktree> worktrees,
        ScriptedOrca orca,
        FakeWrapupClock clock,
        int width,
        IWrapupStore? store = null,
        string template = "Done?",
        TimeSpan? wait = null)
    {
        List<string> said = [];
        var ids = 0;
        var rolling = new WrapupRolling(new WrapupOrca(orca), store ?? new MemoryWrapupStore(), clock, width, wait ?? Wait, () => $"ask{++ids}", "rig");
        var output = await WrapupRun.AskRollingAsync(rows, template, worktrees, rolling, (row, message) => said.Add($"{row.Worktree}: {message}"), Token);
        return (output, said);
    }

    private static Dictionary<(string, string), View> Views(params ((string, string) Key, View View)[] views) => views.ToDictionary(v => v.Key, v => v.View);

    private static async Task<(IReadOnlyList<WrapupRow> Rows, Dictionary<string, WrapupWorktree> Worktrees)> FamilyAsync()
    {
        var parent = Worktree("parent", agents: Agent("t1:l1"));
        var kid = Worktree("kid", parent: "parent", agents: Agent("t2:l2"));
        var rows = await PlanAsync([parent, kid], [Terminal("t1:l1"), Terminal("t2:l2")]);
        return (rows, new() { ["parent"] = parent, ["kid"] = kid });
    }

    private static Dictionary<(string, string), View> FamilyViews() => Views(
        (("kid", "t2:l2"), ScriptedOrca.AnswersAfter(10, "WRAPUP: pending: merge PR #9")),
        (("parent", "t1:l1"), ScriptedOrca.AnswersAfter(15, "WRAPUP: done")));

    [Fact]
    public async Task The_next_session_is_asked_as_soon_as_one_answers()
    {
        var (rows, worktrees) = await SessionsAsync(3);
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, Views(
            (("w0", "t0:l0"), ScriptedOrca.AnswersAfter(10, "WRAPUP: done")),
            (("w1", "t1:l1"), ScriptedOrca.AnswersAfter(20, "WRAPUP: pending: merge PR #9")),
            (("w2", "t2:l2"), ScriptedOrca.AnswersAfter(30, "**WRAPUP: done**"))));
        var store = new MemoryWrapupStore();

        var (output, said) = await RunAsync(rows, worktrees, orca, clock, 2, store);

        Assert.All(output, r => Assert.Equal(WrapupStatus.Answered, r.Status));
        Assert.Equal(["", "merge PR #9", ""], output.Select(r => r.Verdict?.Detail));
        // w2 waited for a free slot: it was asked only once w0 had answered.
        Assert.Equal(["w0: asked", "w1: asked", "w0: answered: done", "w2: asked"], said.Take(4));
        Assert.True(clock.Elapsed < 60);
        var saved = store.Files[rows[1].Path];
        Assert.Equal(("t1:l1", "ask2"), (Assert.Single(saved.Asks).Pane, saved.Asks[0].Id));
        Assert.Equal((saved.Asks[0].At, "merge PR #9"), (saved.Answers[0].AskedAt, saved.Answers[0].Detail));
    }

    [Fact]
    public async Task A_reply_from_before_the_send_is_not_the_answer()
    {
        var (rows, worktrees) = await SessionsAsync(1);
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, Views((("w0", "t0:l0"), ScriptedOrca.AnswersAfter(0, "WRAPUP: done (old)")))) { StaleMs = Ms(5) };

        var (output, said) = await RunAsync(rows, worktrees, orca, clock, 1);

        Assert.Equal(WrapupStatus.Sent, output[0].Status);
        Assert.Equal("no answer within 1m; the next run reads it", output[0].Note);
        Assert.Equal("w0: no reply within 1m, moving on", said[^1]);
        Assert.True(clock.Elapsed >= 60);
    }

    [Fact]
    public async Task A_session_waiting_on_its_owner_frees_its_slot()
    {
        var (rows, worktrees) = await SessionsAsync(2);
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, Views(
            (("w0", "t0:l0"), elapsed => elapsed >= 5 ? ("waiting", $"{Wrapup.Marker} Done?", null) : null),
            (("w1", "t1:l1"), ScriptedOrca.AnswersAfter(10, "WRAPUP: done"))));

        var (output, said) = await RunAsync(rows, worktrees, orca, clock, 1);

        Assert.Equal((WrapupStatus.Waiting, "allow gh pr merge?"), (output[0].Status, output[0].Note));
        Assert.Contains("w0: waiting on you: allow gh pr merge?", said);
        Assert.Equal(WrapupStatus.Answered, output[1].Status);
        Assert.True(clock.Elapsed < 60);
    }

    [Fact]
    public async Task An_idle_reply_without_the_marker_or_a_verdict_line()
    {
        var (rows, worktrees) = await SessionsAsync(2);
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, Views(
            (("w0", "t0:l0"), elapsed => elapsed >= 5 ? ("done", "something else", "ok") : null),
            (("w1", "t1:l1"), ScriptedOrca.AnswersAfter(5, "\nAll merged, nothing left."))));

        var (output, said) = await RunAsync(rows, worktrees, orca, clock, 2);

        Assert.Equal(WrapupStatus.Sent, output[0].Status);
        Assert.Equal((WrapupStatus.Answered, null, "replied without a WRAPUP line: All merged, nothing left."), (output[1].Status, output[1].Verdict, output[1].Note));
        Assert.Contains("w1: answered, without a WRAPUP line", said);
    }

    [Fact]
    public async Task A_parent_waits_for_its_subtasks_and_hears_their_verdicts()
    {
        var (rows, worktrees) = await FamilyAsync();
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, FamilyViews());

        var (output, said) = await RunAsync(rows, worktrees, orca, clock, 5, template: "Kids: $children");

        Assert.Equal(["kid: asked", "kid: answered: pending: merge PR #9", "parent: asked"], said.Take(3));
        Assert.Equal(WrapupStatus.Answered, output[0].Status);
        var parentSend = orca.Sends.ElementAt(1);
        Assert.Equal("term_t1", parentSend[3]);
        Assert.Contains("pending: merge PR #9", parentSend[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orca_going_quiet_mid_run_times_out_rather_than_failing()
    {
        var (rows, worktrees) = await SessionsAsync(1);
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, []) { Down = true };

        var (output, _) = await RunAsync(rows, worktrees, orca, clock, 1);

        Assert.Equal(WrapupStatus.Sent, output[0].Status);
        Assert.StartsWith("no answer within 1m", output[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_sends_do_not_hold_a_slot()
    {
        var (planned, worktrees) = await SessionsAsync(2);
        IReadOnlyList<WrapupRow> rows = [planned[0] with { Terminal = null }, planned[1]];
        var clock = new FakeWrapupClock();
        var orca = new ScriptedOrca(clock, [])
        {
            Override = args => args[1] == "send" && args[3] == "term_t1" ? new Ran(1, """{"ok": false, "error": {"message": "pane closed"}}""", "") : null,
        };

        var (output, said) = await RunAsync(rows, worktrees, orca, clock, 1);

        Assert.Equal([WrapupStatus.SendFailed, WrapupStatus.SendFailed], output.Select(r => r.Status));
        Assert.Equal(["no terminal", "pane closed"], output.Select(r => r.Note));
        Assert.Equal(["w0: send failed: no terminal", "w1: send failed: pane closed"], said);
        Assert.Equal(0, clock.Elapsed);
    }

    [Fact]
    public async Task An_interrupt_leaves_the_unasked_deferred()
    {
        var (rows, worktrees) = await SessionsAsync(2);
        var clock = new FakeWrapupClock { Interrupt = true };

        var (output, _) = await RunAsync(rows, worktrees, new ScriptedOrca(clock, []), clock, 1);

        Assert.Equal([WrapupStatus.Sent, WrapupStatus.Deferred], output.Select(r => r.Status));
        Assert.Equal("interrupted before it was asked", output[1].Note);
    }

    [Fact]
    public async Task Waits_that_are_not_whole_minutes_read_in_seconds()
    {
        var (rows, worktrees) = await SessionsAsync(1);
        var clock = new FakeWrapupClock();

        var (output, _) = await RunAsync(rows, worktrees, new ScriptedOrca(clock, []), clock, 1, wait: TimeSpan.FromSeconds(45));

        Assert.Equal("no answer within 45s; the next run reads it", output[0].Note);
        Assert.Equal(("1m", "45s", "0m"), (WrapupRun.Minutes(Wait), WrapupRun.Minutes(TimeSpan.FromSeconds(45)), WrapupRun.Minutes(TimeSpan.Zero)));
    }

    [Fact]
    public async Task An_answer_the_agent_reported_itself_frees_the_slot_without_a_second_record()
    {
        var (rows, worktrees) = await SessionsAsync(2);
        var clock = new FakeWrapupClock();
        var store = new MemoryWrapupStore();
        // The agent runs `estate wrapup report` the moment it is asked: its answer is in the file before the first poll.
        store.OnWrite = state => state.Asks.Count > 0 && state.Answers.Count == 0
            ? state.WithAnswer(new AnswerRecord("r", state.Asks[^1].Pane, "pending", "land #4", "f00d", state.Asks[^1].At, ["land #4"], "report"))
            : state;

        var (output, said) = await RunAsync(rows, worktrees, new ScriptedOrca(clock, []), clock, 1, store);

        Assert.Equal(["w0: asked", "w0: answered: pending: land #4", "w1: asked", "w1: answered: pending: land #4"], said);
        Assert.All(output, r => Assert.Equal((WrapupStatus.Answered, "report"), (r.Status, r.Recorded!.Via)));
        Assert.Single(store.Files[rows[0].Path].Answers);
        Assert.True(clock.Elapsed < 60);
    }
}

/// <summary>
/// An <c>orca</c> whose sends succeed and whose <c>worktree ps</c> shows each pane through its view, given the seconds since the
/// run began: (state, prompt, reply), or null while the pane has not changed.
/// </summary>
internal sealed class ScriptedOrca(FakeWrapupClock clock, Dictionary<(string Worktree, string Pane), Func<double, (string State, string Prompt, string? Reply)?>> views) : IProc
{
    public List<string[]> Calls { get; } = [];

    public bool Down { get; set; }

    /// <summary>When set, every pane's state started this long ago instead of now: a reply from before the send.</summary>
    public double? StaleMs { get; set; }

    /// <summary>Answers a call before the script does, when it returns one.</summary>
    public Func<string[], Ran?>? Override { get; set; }

    public IEnumerable<string[]> Sends => Calls.Where(c => c[0] == "terminal" && c[1] == "send");

    public static Func<double, (string, string, string?)?> AnswersAfter(double seconds, string reply) =>
        elapsed => elapsed >= seconds ? ("done", $"{Wrapup.Marker} Done?", reply) : null;

    public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string[] args = [.. request.Argv.Skip(1)];
        Calls.Add(args);
        if (Override?.Invoke(args) is { } answered)
        {
            return Task.FromResult(answered);
        }

        if (args[0] == "terminal" && args[1] == "send")
        {
            return Task.FromResult(new Ran(0, """{"ok": true}""", ""));
        }

        if (Down)
        {
            return Task.FromResult(new Ran(1, "", "orca is not running"));
        }

        var worktrees = new JsonArray();
        foreach (var ((wid, pane), view) in views)
        {
            if (view(clock.Elapsed) is not var (state, prompt, reply))
            {
                continue;
            }

            var agent = new JsonObject
            {
                ["paneKey"] = pane,
                ["state"] = state,
                ["agentType"] = "claude",
                ["prompt"] = prompt,
                ["lastAssistantMessage"] = reply,
                ["stateStartedAt"] = StaleMs ?? clock.Now.ToUnixTimeMilliseconds(),
            };
            if (state == "waiting")
            {
                agent["programStatus"] = new JsonObject { ["state"] = "blocked", ["kind"] = "permission", ["message"] = "allow gh pr merge?" };
            }

            worktrees.Add(new JsonObject { ["worktreeId"] = wid, ["repoId"] = "rig-id", ["path"] = $"/w/{wid}", ["agents"] = new JsonArray(agent) });
        }

        return Task.FromResult(new Ran(0, new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["worktrees"] = worktrees } }.ToJsonString(), ""));
    }
}
