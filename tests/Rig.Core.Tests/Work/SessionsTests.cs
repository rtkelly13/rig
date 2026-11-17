using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Text;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

/// <summary>
/// The session reader against what the Python reader said about the same files (<c>work-sessions.fixture.json</c>,
/// written by <c>desktop_manager.work.session_store</c> before it was retired).
/// </summary>
public sealed class SessionsTests
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> TranscriptCases() => [.. Fixture()["transcripts"]!.AsArray().Select(entry => (string)entry!["name"]!)];

    [Theory]
    [MemberData(nameof(TranscriptCases))]
    public void A_transcript_is_summarised_as_python_summarised_it(string name)
    {
        var entry = Fixture()["transcripts"]!.AsArray().Single(item => (string)item!["name"]! == name)!;
        var expected = entry["expected"]!;

        var record = Transcripts.Summarise(name, entry["lines"]!.AsArray().Select(line => (string)line!));

        Assert.Equal((string)expected["session_id"]!, record.SessionId);
        Assert.Equal((string?)expected["start_cwd"], record.StartCwd);
        Assert.Equal((string?)expected["last_cwd"], record.LastCwd);
        Assert.Equal((string?)expected["title"], record.Title);
        Assert.Equal((string?)expected["first_prompt"], record.FirstPrompt);
        Assert.Equal((int)expected["prompt_count"]!, record.PromptCount);
        Assert.Equal((string?)expected["last_activity"], IsoTime.Format(record.LastActivity));
        Assert.Equal((string)expected["exit_kind"]! == "exited", record.ExitedCleanly);
        Assert.Equal((bool)expected["interactive"]!, record.Interactive);
        Assert.Equal((string)expected["turn_state"]!, record.TurnState);
        Assert.Equal((string)expected["label"]!, record.Label);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task A_transcript_file_reads_the_same_with_either_line_ending(string newline)
    {
        using var temp = new TempDir();
        var entry = Fixture()["transcripts"]!.AsArray().Single(item => (string)item!["name"]! == "bookkeeping-after-exit")!;
        var lines = entry["lines"]!.AsArray().Select(line => (string)line!).ToList();
        var path = Path.Join(temp.Path, "0f8fad5b-d9cb-469f-a165-70867728950e.jsonl");
        await File.WriteAllTextAsync(path, string.Join(newline, lines) + newline, Utf8, Token);

        var record = await Transcripts.ReadAsync(path, Token);

        Assert.Equal(Transcripts.Summarise("0f8fad5b-d9cb-469f-a165-70867728950e", lines), record);
    }

    [Fact]
    public void A_deeply_nested_line_is_still_read()
    {
        var nested = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);
        var line = $$"""{"type":"user","cwd":"/code","message":{"content":"deep"},"toolUseResult":{{nested}}}""";

        var record = Transcripts.Summarise("deep", [line]);

        Assert.Equal(1, record.PromptCount);
    }

    [Fact]
    public void A_label_never_splits_a_character_outside_the_basic_plane()
    {
        var prompt = string.Concat(Enumerable.Repeat("\U0001F600", 61));
        var line = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["content"] = prompt } }.ToJsonString();

        var record = Transcripts.Summarise("emoji", [line]);

        Assert.Equal(string.Concat(Enumerable.Repeat("\U0001F600", 60)), record.Label);
    }

    [Fact]
    public async Task A_claude_home_is_read_as_python_read_it()
    {
        using var temp = new TempDir();
        var home = Fixture()["home"]!;
        WriteHome(temp.Path, home);
        var alive = home["alive"]!.AsArray().Select(entry => ((int)entry!["pid"]!, (string?)entry["proc_start"])).ToHashSet();
        var source = new ClaudeWorkSessions(temp.Path, Path.Join(temp.Path, "session-ends"), (pid, start) => alive.Contains((pid, start)));

        var scan = await source.ReadAsync(DateTimeOffset.Parse((string)home["since"]!, System.Globalization.CultureInfo.InvariantCulture), Token);

        var expected = home["expected"]!;
        Assert.Equal((int)expected["transcripts_read"]!, scan.TranscriptsRead);
        Assert.Equal(
            expected["sessions"]!.AsArray().Select(session => session!.ToJsonString()),
            scan.Sessions.OrderBy(session => session.SessionId, StringComparer.Ordinal).Select(Wire));
    }

    [Fact]
    public async Task A_home_without_any_claude_folders_has_no_sessions()
    {
        using var temp = new TempDir();
        var source = new ClaudeWorkSessions(temp.Path, Path.Join(temp.Path, "session-ends"), (_, _) => true);

        var scan = await source.ReadAsync(DateTimeOffset.MinValue, Token);

        Assert.Empty(scan.Sessions);
        Assert.Equal(0, scan.TranscriptsRead);
    }

    [Fact]
    public async Task A_transcript_that_cannot_be_read_is_skipped_and_not_counted()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            // A Windows directory named like a transcript is never offered as a file, and mode bits do not lock one.
            return;
        }

        using var temp = new TempDir();
        var locked = temp.File("projects/p/locked.jsonl");
        await File.WriteAllTextAsync(temp.File("projects/p/open.jsonl"), """{"type":"user","message":{"content":"hello"}}""", Utf8, Token);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var source = new ClaudeWorkSessions(temp.Path, Path.Join(temp.Path, "session-ends"), (_, _) => false);
        try
        {
            var scan = await source.ReadAsync(DateTimeOffset.MinValue, Token);

            Assert.Equal(1, scan.TranscriptsRead);
            Assert.Equal("open", Assert.Single(scan.Sessions).SessionId);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task A_project_folder_that_cannot_be_read_does_not_hide_the_other_projects()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            // Mode bits do not lock a Windows folder.
            return;
        }

        using var temp = new TempDir();
        await File.WriteAllTextAsync(temp.File("projects/locked/hidden.jsonl"), """{"type":"user","message":{"content":"hidden"}}""", Utf8, Token);
        await File.WriteAllTextAsync(temp.File("projects/open/seen.jsonl"), """{"type":"user","message":{"content":"hello"}}""", Utf8, Token);
        var locked = Path.Join(temp.Path, "projects", "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        var source = new ClaudeWorkSessions(temp.Path, Path.Join(temp.Path, "session-ends"), (_, _) => false);
        try
        {
            var scan = await source.ReadAsync(DateTimeOffset.MinValue, Token);

            Assert.Equal("seen", Assert.Single(scan.Sessions).SessionId);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task A_registry_file_that_cannot_be_read_says_nothing()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return;
        }

        using var temp = new TempDir();
        var locked = temp.File("sessions/1.json");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.Empty(await SessionRegistry.ReadLiveAsync(Path.Join(temp.Path, "sessions"), (_, _) => true, Token));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void The_claude_home_follows_the_environment_then_the_user_home()
    {
        var configured = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = "/elsewhere", ["CLAUDE_SESSION_LEDGER_DIR"] = "/ledger" };
        var bare = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = string.Empty };

        Assert.Equal("/elsewhere", ClaudeWorkSessions.Home(configured.GetValueOrDefault, "/home/me"));
        Assert.Equal(Path.Join("/home/me", ".claude"), ClaudeWorkSessions.Home(bare.GetValueOrDefault, "/home/me"));
        Assert.Equal(Path.Join("/home/me", ".claude"), ClaudeWorkSessions.Home(_ => null, "/home/me"));
        Assert.Equal("/ledger", ClaudeWorkSessions.LedgerDirectory(configured.GetValueOrDefault, "/elsewhere"));
        Assert.Equal(Path.Join("/elsewhere", "session-ends"), ClaudeWorkSessions.LedgerDirectory(_ => string.Empty, "/elsewhere"));
        Assert.Equal(Path.Join("/elsewhere", "session-ends"), ClaudeWorkSessions.LedgerDirectory(bare.GetValueOrDefault, "/elsewhere"));
    }

    [Fact]
    public void This_process_is_alive_and_one_that_has_exited_is_not()
    {
        using var child = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var pid = child.Id;
        child.WaitForExit();

        Assert.True(ProcessLiveness.IsAlive(Environment.ProcessId, null));
        Assert.False(ProcessLiveness.IsAlive(pid, null));
    }

    [Fact]
    public void A_recycled_pid_is_not_alive_where_the_start_time_can_be_compared()
    {
        Assert.Equal(!OperatingSystem.IsWindows(), ProcessLiveness.IsAlive(Environment.ProcessId, "1"));
    }

    [Fact]
    public void A_folder_inside_a_checkout_resolves_to_its_repo_and_branch()
    {
        using var temp = new TempDir();
        var proc = new FakeProc()
            .Answer(new Ran(0, "/code/elsewhere/repo\n.git\n.git\n", string.Empty), "git", "rev-parse")
            .Answer(new Ran(0, "feature/x\n", string.Empty), "git", "branch");

        var found = SessionFolders.Resolve(new Git(proc, temp.Path), temp.Path);

        Assert.Equal(new Attribution("/code/elsewhere/repo".Replace('/', Path.DirectorySeparatorChar), "feature/x"), found);
        Assert.All(proc.Requests, request => Assert.Equal(TimeSpan.FromSeconds(10), request.Timeout));
    }

    [Fact]
    public void A_detached_checkout_resolves_with_no_branch()
    {
        using var temp = new TempDir();
        var proc = new FakeProc()
            .Answer(new Ran(0, "/code/repo\r\n.git\r\n.git\r\n", string.Empty), "git", "rev-parse")
            .Answer(new Ran(0, "\n", string.Empty), "git", "branch");

        var found = SessionFolders.Resolve(new Git(proc, temp.Path), temp.Path);

        Assert.Equal(string.Empty, found?.Branch);
    }

    [Fact]
    public void A_folder_outside_git_or_gone_resolves_to_nothing()
    {
        using var temp = new TempDir();
        var proc = new FakeProc().Answer(new Ran(128, string.Empty, "fatal: not a git repository"), "git", "rev-parse");
        var git = new Git(proc, temp.Path);

        Assert.Null(SessionFolders.Resolve(git, temp.Path));
        Assert.Null(SessionFolders.Resolve(git, Path.Join(temp.Path, "gone")));
        Assert.Single(proc.Requests);
    }

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Work", "work-sessions.fixture.json")))!.AsObject();

    private static void WriteHome(string root, JsonNode home)
    {
        foreach (var transcript in home["transcripts"]!.AsArray())
        {
            var path = Path.Join(root, "projects", ((string)transcript!["path"]!).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Concat(transcript["lines"]!.AsArray().Select(line => (string)line! + "\n")), Utf8);
            File.SetLastWriteTimeUtc(path, DateTimeOffset.Parse((string)transcript["modified"]!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
        }

        foreach (var (folder, key) in new[] { ("sessions", "registry"), ("session-ends", "ledger") })
        {
            Directory.CreateDirectory(Path.Join(root, folder));
            foreach (var (name, text) in home[key]!.AsObject())
            {
                File.WriteAllText(Path.Join(root, folder, name), (string)text!, Utf8);
            }
        }
    }

    // A session as the Python fixture spells it, key for key.
    private static string Wire(SessionFacts session) =>
        new JsonObject
        {
            ["session_id"] = session.SessionId,
            ["title"] = session.Title,
            ["state"] = session.State.Wire(),
            ["last_activity"] = IsoTime.Format(session.LastActivity),
            ["last_cwd"] = session.LastCwd,
            ["start_cwd"] = session.StartCwd,
            ["prompt_count"] = session.PromptCount,
            ["pid"] = session.Pid,
        }.ToJsonString();
}
