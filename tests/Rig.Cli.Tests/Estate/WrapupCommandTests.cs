using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Estate;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate wrapup</c> and <c>wrapup report</c> end to end. Orca is a <see cref="FakeProc"/>, git runs for real against
/// temp repos, and the follower is faked, so no real Orca, agent or background process is reached. Ported from the command
/// tests in <c>tests/test_estate_wrapup*.py</c>.
/// </summary>
public sealed class WrapupCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = WrapupFixtures.Now;

    private readonly TempDir _temp = new();
    private readonly string _root;
    private readonly string _code;
    private readonly FakeProc _orca = new();
    private readonly Follower _follower = new();
    private readonly FakeWrapupClock _clock = new();
    private string _w1 = "";
    private string _w2 = "";

    public WrapupCommandTests()
    {
        _root = PathResolution.Resolve(_temp.Path);
        _code = Path.Join(_root, "code");
        _ = Directory.CreateDirectory(Path.Join(_root, "rig", "config"));
        File.WriteAllText(Path.Join(_root, "rig", "config", EstateScopes.FileName), """{"personal": {"owners": ["rtkelly13"], "orgs": ["rtkelly-labs"]}}""");
        File.WriteAllText(TemplateFile, "Is $worktree done? Run $report");
        _ = Directory.CreateDirectory(Path.Join(Home, ".workspace"));
        File.WriteAllText(Path.Join(Home, ".workspace", "config.json"), """{"tools": {"estate": {"work_dirs": ["acme"]}}}""");
        Terminals(Terminal("t1", "l1"), Terminal("t2", "l2"));
        _orca.Answer(
            new Ran(0, $$$"""{"ok": true, "result": {"repos": [{"id": "rig-id", "path": {{{Json(Path.Join(_code, "personal", "rig"))}}}}, {"id": "work-id", "path": {{{Json(Path.Join(_code, "acme", "platform"))}}}}]}}""", ""),
            "orca", "repo", "list");
        _orca.Answer(new Ran(0, """{"ok": true, "result": {"submitted": true}}""", ""), "orca", "terminal", "send");
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Home => Path.Join(_root, "home");

    private string TemplateFile => Path.Join(_root, "rig", "config", Wrapup.TemplateFile);

    private string[][] Sends => [.. _orca.Requests.Where(r => r.Argv.Count > 2 && r.Argv[1] == "terminal" && r.Argv[2] == "send").Select(r => r.Argv.ToArray())];

    [Theory]
    [InlineData("--all", "pass exactly one of --personal or --work")]
    [InlineData("--personal --work --all", "pass exactly one of --personal or --work")]
    [InlineData("--personal --all --repo rig", "--all and --repo are exclusive")]
    [InlineData("--personal", "name a target: --repo NAME (repeatable), --worktree NAME, or --all")]
    [InlineData("--personal --all --wait soon", "'soon': expected a duration such as 30m, 2h or 45s")]
    [InlineData("--personal --all --idle x'y", "\"x'y\": expected a duration such as 30m, 2h or 45s")]
    public async Task Wrapup_refuses_an_unclear_scope_target_or_duration(string args, string message)
    {
        var (code, output, error) = await RunAsync(["estate", "wrapup", .. args.Split(' ')]);

        Assert.Equal((2, "", $"Invalid value: {message}\n"), (code, output, error));
        Assert.Empty(_orca.Requests);
    }

    [Fact]
    public async Task A_dry_run_lists_each_session_and_sends_nothing()
    {
        await TwoSessionsAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--json"]);

        Assert.Equal(0, code);
        var rows = JsonNode.Parse(output)!.AsArray();
        Assert.Equal(["w1", "w2"], rows.Select(r => (string)r!["worktree"]!));
        Assert.Equal([0, 1], rows.Select(r => (int)r!["depth"]!));
        Assert.All(rows, r => Assert.Equal("ask", (string)r!["status"]!));
        Assert.Equal(("rtkelly13/w1", 120, 1), ((string)rows[0]!["branch"]!, (int)rows[0]!["idle_minutes"]!, (int)rows[0]!["open_children"]!));
        Assert.Contains("\"facts\":\"clean; 2 commit(s) on no remote; no PR\"", output, StringComparison.Ordinal);
        Assert.Empty(Sends);
        Assert.False(File.Exists(WorktreeState.StateFile(_w1, WrapupState.Feature)));
    }

    [Fact]
    public async Task The_table_counts_statuses_and_says_how_apply_would_ask()
    {
        await TwoSessionsAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--repo", "RIG", "--show-prompt"]);

        Assert.Equal(0, code);
        Assert.Contains("── rig/w2: session t2:l2 ──\n[Wrap-up check] Is w2 done? Run rig estate wrapup report\n", output, StringComparison.Ordinal);
        Assert.Contains("Orca wrap-up (DRY-RUN (pass --apply to send))", output, StringComparison.Ordinal);
        Assert.Contains("1 of the subtask sessions not finished", output, StringComparison.Ordinal);
        Assert.EndsWith("2 ask\n--apply asks these 2, 5 at a time, waiting up to 10m each\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_matching_says_so()
    {
        await TwoSessionsAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--repo", "design-system"]);

        Assert.Equal((0, "No matching Claude sessions in Orca.\n"), (code, output));
    }

    [Fact]
    public async Task Apply_without_waiting_sends_the_deepest_first_and_records_each_ask()
    {
        await TwoSessionsAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--apply", "--wait", "0", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal(["term_t2", "term_t1"], Sends.Select(s => s[4]));
        Assert.Equal("[Wrap-up check] Is w2 done? Run rig estate wrapup report --ask ask1", Sends[0][6]);
        Assert.All(JsonNode.Parse(output)!.AsArray(), r => Assert.Equal("sent", (string)r!["status"]!));
        var state = new FileWrapupStore(new Git(Repos.Proc, _root)).Read(_w2)!;
        var ask = Assert.Single(state.Asks);
        Assert.Equal(("w2", "rtkelly13/w2", "ask1", "t2:l2"), (state.WorktreeId, state.Branch, ask.Id, ask.Pane));
        Assert.Equal(WrapupStamps.Timestamp(Now), ask.At);
    }

    [Fact]
    public async Task Wait_zero_keeps_the_per_run_cap()
    {
        await TwoSessionsAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--wait", "0", "--max", "1", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal(["deferred", "ask"], JsonNode.Parse(output)!.AsArray().Select(r => (string)r!["status"]!));
    }

    [Fact]
    public async Task A_failed_send_exits_one_and_says_why()
    {
        await TwoSessionsAsync();
        _orca.Answer(new Ran(1, """{"ok": false, "error": {"message": "pane closed"}}""", ""), "orca", "terminal", "send");

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--worktree", "w1", "--apply", "--wait", "0"]);

        Assert.Equal(1, code);
        Assert.Contains("APPLIED", output, StringComparison.Ordinal);
        Assert.Contains("pane closed", output, StringComparison.Ordinal);
        Assert.EndsWith("1 send-failed\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_work_scope_reads_the_machines_work_folders()
    {
        var platform = await Repos.InitAsync(Path.Join(_code, "acme", "platform"));
        Worktrees(Worktree("platform", platform, "work-id", null, Agent("t1:l1")), Worktree("p", Path.Join(_code, "personal", "p"), "rig-id", null, Agent("t2:l2")));

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--work", "--all", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal(["platform"], JsonNode.Parse(output)!.AsArray().Select(r => (string)r!["worktree"]!));
    }

    [Fact]
    public async Task A_missing_template_or_a_stopped_orca_is_an_error()
    {
        var (missing, _, missingError) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--template", Path.Join(_root, "gone.md")]);
        _orca.Answer(new Ran(1, "", "orca is not running"), "orca", "worktree", "ps");
        var (down, _, downError) = await RunAsync(["estate", "wrapup", "--personal", "--all"]);

        Assert.Equal(1, missing);
        Assert.StartsWith($"Cannot read the prompt template {Path.Join(_root, "gone.md").Replace('\\', '/')}: ", missingError, StringComparison.Ordinal);
        Assert.Equal((3, "orca worktree ps failed: orca is not running\n"), (down, downError));
    }

    [Fact]
    public async Task A_truncated_list_is_called_out()
    {
        await TwoSessionsAsync(truncated: true);

        var (_, _, error) = await RunAsync(["estate", "wrapup", "--personal", "--all"]);

        Assert.Equal("Orca returned only part of the worktree list; some sessions are missing.\n", error);
    }

    [Fact]
    public async Task A_run_records_new_answers_once()
    {
        await TwoSessionsAsync(reply: "WRAPUP: pending: merge #9", prompt: $"{Wrapup.Marker} done?");

        var (code, output, _) = await RunAsync(["estate", "wrapup", "--personal", "--worktree", "w1", "--json"]);
        _ = await RunAsync(["estate", "wrapup", "--personal", "--worktree", "w1", "--json"]);

        Assert.Equal(0, code);
        Assert.Contains("\"status\":\"answered\"", output, StringComparison.Ordinal);
        Assert.Contains("\"verdict\":\"pending\",\"verdict_detail\":\"merge #9\"", output, StringComparison.Ordinal);
        var answer = Assert.Single(new FileWrapupStore(new Git(Repos.Proc, _root)).Read(_w1)!.Answers);
        Assert.Equal(("pending", "merge #9", "reply", (string?)null), (answer.Verdict, answer.Detail, answer.Via, answer.AskedAt));
    }

    [Fact]
    public async Task Apply_hands_the_asking_to_a_background_follower_with_the_same_targets()
    {
        await TwoSessionsAsync();

        var (code, output, error) = await RunAsync(["estate", "wrapup", "--personal", "--repo", "rig", "--worktree", "w1", "--children", "--apply"]);

        Assert.Equal(0, code);
        Assert.Contains("ASKING IN THE BACKGROUND", output, StringComparison.Ordinal);
        Assert.Equal(
            ["--personal", "--repo", "rig", "--worktree", "w1", "--children", "--idle", "30m", "--max", "5", "--wait", "10m", "--template", TemplateFile, "--apply"],
            Assert.Single(_follower.Starts));
        Assert.StartsWith("Asking 2 in the background, 5 at a time, up to 10m each. Log: /logs/follow.log\n", error, StringComparison.Ordinal);
        Assert.Empty(Sends);
    }

    [Fact]
    public async Task Apply_never_starts_a_second_follower_or_one_with_nothing_to_ask()
    {
        await TwoSessionsAsync();
        _follower.IsRunning = true;

        var (running, _, error) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--apply"]);
        var (idle, _, _) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--apply", "--idle", "1d"]);

        Assert.Equal(1, running);
        Assert.StartsWith("A wrap-up follower is already asking sessions; nothing new was started. Its log is in /logs.", error, StringComparison.Ordinal);
        Assert.Equal(0, idle);
        Assert.Empty(_follower.Starts);
    }

    [Fact]
    public async Task Follow_asks_in_this_terminal_with_progress_on_stderr()
    {
        await TwoSessionsAsync();

        var (code, output, error) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--apply", "--follow", "--wait", "30s", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal(["term_t2", "term_t1"], Sends.Select(s => s[4]));
        Assert.Equal(
            "rig/w2: asked\nrig/w2: no reply within 30s, moving on\nrig/w1: asked\nrig/w1: no reply within 30s, moving on\n",
            error);
        Assert.All(JsonNode.Parse(output)!.AsArray(), r => Assert.StartsWith("no answer within 30s", (string)r!["note"]!, StringComparison.Ordinal));
        Assert.False(_follower.Held);
    }

    [Fact]
    public async Task Follow_stops_when_another_follower_holds_the_lock()
    {
        await TwoSessionsAsync();
        _follower.Locked = true;

        var (code, _, error) = await RunAsync(["estate", "wrapup", "--personal", "--all", "--apply", "--follow"]);

        Assert.Equal((1, "Another wrap-up follower is asking sessions; this one stops.\n"), (code, error));
        Assert.Empty(Sends);
    }

    [Fact]
    public async Task Report_records_the_agents_verdict_in_its_worktree()
    {
        await AskedAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "report", "--ask", "a1", "--pending", "merge #9", "--todo", "merge #9", "--todo", "close #4"], _w1);

        Assert.Equal((0, "Recorded for w1: pending: merge #9\n"), (code, output));
        var answer = Assert.Single(new FileWrapupStore(new Git(Repos.Proc, _root)).Read(_w1)!.Answers);
        Assert.Equal(("report", (await Repos.GitAsync(_w1, "rev-parse", "HEAD")), "2026-11-07T11:00:00.000+00:00"), (answer.Via, answer.Head, answer.AskedAt));
        Assert.Equal(["merge #9", "close #4"], answer.Todo);
    }

    [Fact]
    public async Task Report_done_records_done()
    {
        await AskedAsync();

        var (code, output, _) = await RunAsync(["estate", "wrapup", "report", "--done"], _w1);

        Assert.Equal((0, "Recorded for w1: done\n"), (code, output));
    }

    [Theory]
    [InlineData("", "pass exactly one of --done or --pending TEXT")]
    [InlineData("--done --pending x", "pass exactly one of --done or --pending TEXT")]
    [InlineData("--pending  ", "--pending needs a summary of what is left, or at least one --todo")]
    public async Task Report_refuses_an_unclear_verdict(string args, string message)
    {
        string[] argv = args.Length == 0 ? [] : args == "--pending  " ? ["--pending", " "] : args.Split(' ');

        var (code, _, error) = await RunAsync(["estate", "wrapup", "report", .. argv], _root);

        Assert.Equal((2, $"Invalid value: {message}\n"), (code, error));
    }

    [Fact]
    public async Task Report_refuses_an_unknown_ask_or_a_folder_outside_git()
    {
        await AskedAsync();

        var (unknown, _, unknownError) = await RunAsync(["estate", "wrapup", "report", "--ask", "zz", "--done"], _w1);
        var (outside, _, outsideError) = await RunAsync(["estate", "wrapup", "report", "--done"], _root);

        Assert.Equal((1, "Not recorded: no ask zz is recorded in this worktree. Reply with the WRAPUP line from the prompt instead.\n"), (unknown, unknownError));
        Assert.Equal((1, "Not inside a git worktree: run this from the worktree you were asked about.\n"), (outside, outsideError));
    }

    private static string Json(string text) => JsonValue.Create(text).ToJsonString();

    private static JsonObject Terminal(string tab, string leaf) =>
        new() { ["handle"] = $"term_{tab}", ["tabId"] = tab, ["leafId"] = leaf, ["writable"] = true, ["agentIdentity"] = "claude" };

    private static JsonObject Agent(string pane, string prompt = "do the thing", string reply = "Did it.") => new()
    {
        ["paneKey"] = pane,
        ["state"] = "done",
        ["agentType"] = "claude",
        ["prompt"] = prompt,
        ["lastAssistantMessage"] = reply,
        ["conversationName"] = $"session {pane}",
        ["stateStartedAt"] = (Now - TimeSpan.FromMinutes(120)).ToUnixTimeMilliseconds(),
    };

    private static JsonObject Worktree(string id, string path, string repoId, string? parent, JsonObject agent) => new()
    {
        ["worktreeId"] = id,
        ["repoId"] = repoId,
        ["repo"] = repoId == "rig-id" ? "rig" : "platform",
        ["path"] = path,
        ["displayName"] = id,
        ["branch"] = $"refs/heads/rtkelly13/{id}",
        ["parentWorktreeId"] = parent,
        ["agents"] = new JsonArray(agent),
    };

    private async Task<string> RepoAsync(string name)
    {
        var path = await Repos.InitAsync(Path.Join(_code, "personal", name));
        await File.WriteAllTextAsync(Path.Join(path, ".gitignore"), "temp/\n", Token);
        await Repos.CommitAsync(path, "ignore");
        return path;
    }

    private async Task TwoSessionsAsync(bool truncated = false, string prompt = "do the thing", string reply = "Did it.")
    {
        _w1 = await RepoAsync("w1");
        _w2 = await RepoAsync("w2");
        Worktrees(truncated, Worktree("w1", _w1, "rig-id", null, Agent("t1:l1", prompt, reply)), Worktree("w2", _w2, "rig-id", "w1", Agent("t2:l2")));
    }

    private async Task AskedAsync()
    {
        _w1 = await RepoAsync("w1");
        var ask = new AskRecord("2026-11-07T11:00:00.000+00:00", "t1:l1", "s", "abc", "clean", "0123456789ab", "a1");
        Assert.Null(await new FileWrapupStore(new Git(Repos.Proc, _root)).WriteAsync(_w1, WrapupState.Empty("w1", "b").WithAsk(ask), Token));
    }

    private void Worktrees(params JsonObject[] worktrees) => Worktrees(false, worktrees);

    private void Worktrees(bool truncated, params JsonObject[] worktrees) =>
        _orca.Answer(
            new Ran(0, new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["truncated"] = truncated, ["worktrees"] = new JsonArray(worktrees) } }.ToJsonString(), ""),
            "orca", "worktree", "ps");

    private void Terminals(params JsonObject[] terminals) =>
        _orca.Answer(new Ran(0, new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["terminals"] = new JsonArray(terminals) } }.ToJsonString(), ""), "orca", "terminal", "list");

    private async Task<(int Code, string Out, string Err)> RunAsync(string[] args, string? workingDirectory = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var ids = 0;
        var command = new EstateCommand(
            new EstateHost(_code, Home, workingDirectory ?? _root, Path.Join(_root, "rig", "config", EstateScopes.FileName), Path.Join(Home, ".workspace", "config.json"), ManagedProjectsFile: Path.Join(_root, "rig", "config", "managed-projects.json")),
            NoOrca.Host,
            new Routed(_orca),
            new FakeGitHub(),
            new ManualClock(Now),
            _ => null,
            output,
            error,
            new WrapupHost("rig", _follower, _clock, () => $"ask{++ids}"));
        var code = await new RootCommand { command.Build() }.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    // Orca is faked; git runs for real against the temp repos.
    private sealed class Routed(FakeProc orca) : IProc
    {
        public Task<Ran> RunAsync(ProcRequest request, CancellationToken cancellationToken = default) =>
            request.Argv[0] == "orca" ? orca.RunAsync(request, cancellationToken) : Repos.Proc.RunAsync(request, cancellationToken);
    }

    private sealed class Follower : IWrapupFollower
    {
        public List<IReadOnlyList<string>> Starts { get; } = [];

        public bool IsRunning { get; set; }

        public bool Locked { get; set; }

        public bool Held { get; private set; }

        public string LogDir => "/logs";

        public bool Running() => IsRunning;

        public IDisposable? TryHold()
        {
            if (Locked)
            {
                return null;
            }

            Held = true;
            return new Release(this);
        }

        public Task<string> StartAsync(IReadOnlyList<string> args, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Starts.Add(args);
            return Task.FromResult("/logs/follow.log");
        }

        private sealed class Release(Follower follower) : IDisposable
        {
            public void Dispose() => follower.Held = false;
        }
    }
}
