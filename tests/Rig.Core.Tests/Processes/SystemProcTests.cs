using System.Diagnostics;
using Rig.Core.Processes;

namespace Rig.Core.Tests.Processes;

/// <summary>Real processes. git is on every CI runner and dev machine, so it stands in for "a program".</summary>
public sealed class SystemProcTests
{
    private readonly SystemProc _proc = new();

    [Theory]
    [InlineData(new[] { @"C:\npm\opencode.cmd", "models" }, true, "/d /s /c \"C:\\npm\\opencode.cmd models\"")]
    [InlineData(new[] { @"C:\npm\codex.CMD", "fix a & b", "", "say \"hi\"" }, true, "/d /s /c \"C:\\npm\\codex.CMD \"fix a & b\" \"\" \"say \"\"hi\"\"\"\"")]
    [InlineData(new[] { @"C:\Program Files\x.bat" }, true, "/d /s /c \"\"C:\\Program Files\\x.bat\"\"")]
    [InlineData(new[] { "opencode.exe", "models" }, true, null)]
    [InlineData(new[] { "/usr/bin/opencode.cmd" }, false, null)]
    public void A_windows_batch_file_runs_through_cmd(string[] argv, bool windows, string? line) =>
        Assert.Equal(line, SystemProc.BatchLine(argv, windows));

    [Fact]
    public async Task Captures_standard_output()
    {
        var ran = await _proc.RunAsync(new ProcRequest(["git", "--version"]), TestContext.Current.CancellationToken);

        Assert.True(ran.Ok);
        Assert.StartsWith("git version", ran.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_a_failing_exit_code_without_throwing()
    {
        var ran = await _proc.RunAsync(new ProcRequest(["git", "not-a-command"]), TestContext.Current.CancellationToken);

        Assert.Equal(1, ran.Code);
        Assert.Contains("not-a-command", ran.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_attached_process_keeps_its_exit_code_and_captures_nothing()
    {
        var ran = await _proc.RunAsync(new ProcRequest(["git", "not-a-command"]) { Attached = true }, TestContext.Current.CancellationToken);

        Assert.Equal(1, ran.Code);
        Assert.Empty(ran.Out);
        Assert.Empty(ran.Err);
    }

    [Fact]
    public async Task An_attached_process_cannot_be_given_stdin_text()
    {
        var request = new ProcRequest(["git", "--version"]) { Attached = true, StdinText = "x" };

        await Assert.ThrowsAsync<ArgumentException>(() => _proc.RunAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_process_on_the_console_s_input_still_has_its_output_captured()
    {
        var ran = await _proc.RunAsync(new ProcRequest(["git", "--version"]) { ConsoleInput = true }, TestContext.Current.CancellationToken);

        Assert.StartsWith("git version", ran.Out, StringComparison.Ordinal);
        Assert.Empty(ran.Err);
    }

    [Fact]
    public async Task A_process_on_the_console_s_input_cannot_be_given_stdin_text()
    {
        var request = new ProcRequest(["git", "--version"]) { ConsoleInput = true, StdinText = "x" };

        await Assert.ThrowsAsync<ArgumentException>(() => _proc.RunAsync(request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_program_is_127()
    {
        var ran = await _proc.RunAsync(new ProcRequest(["rig-no-such-program"]), TestContext.Current.CancellationToken);

        Assert.Equal(Ran.NotFound, ran.Code);
        Assert.Equal("rig-no-such-program is not on PATH", ran.Err);
    }

    [Fact]
    public async Task Writes_stdin_text()
    {
        var ran = await _proc.RunAsync(
            new ProcRequest(["git", "hash-object", "--stdin"]) { StdinText = "hello\n" },
            TestContext.Current.CancellationToken);

        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", ran.OutOrEmpty);
    }

    [Fact]
    public async Task Adds_to_the_inherited_environment()
    {
        var ran = await _proc.RunAsync(
            new ProcRequest(["git", "config", "--get", "rig.test"])
            {
                Environment = new Dictionary<string, string>
                {
                    ["GIT_CONFIG_COUNT"] = "1",
                    ["GIT_CONFIG_KEY_0"] = "rig.test",
                    ["GIT_CONFIG_VALUE_0"] = "yes",
                },
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("yes", ran.OutOrEmpty);
    }

    [Fact]
    public async Task Runs_in_the_working_directory()
    {
        using var dir = new TempDir();

        var ran = await _proc.RunAsync(new ProcRequest(["git", "init", "-q", "."]) { WorkingDirectory = dir.Path }, TestContext.Current.CancellationToken);

        Assert.True(ran.Ok, ran.Err);
        Assert.True(Directory.Exists(Path.Combine(dir.Path, ".git")));
    }

    [Fact]
    public async Task A_timeout_still_fires_when_a_grandchild_holds_the_output_open()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh(1) is a Unix program.");

        // sh exits at once, but the background sleep inherits stdout, so the output never reaches EOF.
        var ran = await _proc.RunAsync(
            new ProcRequest(["sh", "-c", "sleep 30 &"]) { Timeout = TimeSpan.FromMilliseconds(500) },
            TestContext.Current.CancellationToken);

        Assert.Equal(Ran.TimedOut, ran.Code);
    }

    [Fact]
    public async Task A_timeout_stops_the_process_and_is_124()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sleep(1) is a Unix program.");

        var ran = await _proc.RunAsync(
            new ProcRequest(["sleep", "10"]) { Timeout = TimeSpan.FromMilliseconds(200) },
            TestContext.Current.CancellationToken);

        Assert.Equal(Ran.TimedOut, ran.Code);
        Assert.Equal("sleep timed out after 0s", ran.Err);
    }

    [Fact]
    public async Task Stdin_larger_than_a_pipe_is_echoed_without_deadlock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "cat(1) is a Unix program.");
        var text = new string('x', 1 << 20);

        var ran = await _proc.RunAsync(
            new ProcRequest(["cat"]) { StdinText = text, Timeout = TimeSpan.FromSeconds(30) },
            TestContext.Current.CancellationToken);

        Assert.True(ran.Ok);
        Assert.Equal(text.Length, ran.Out.Length);
    }

    [Fact]
    public async Task The_timeout_covers_a_child_that_neither_reads_nor_exits()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sleep(1) is a Unix program.");
        var watch = Stopwatch.StartNew();

        var ran = await _proc.RunAsync(
            new ProcRequest(["sleep", "10"]) { StdinText = new string('x', 1 << 20), Timeout = TimeSpan.FromMilliseconds(200) },
            TestContext.Current.CancellationToken);

        Assert.Equal(Ran.TimedOut, ran.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Input_the_child_never_reads_is_not_an_error()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "true(1) is a Unix program.");

        var ran = await _proc.RunAsync(
            new ProcRequest(["true"]) { StdinText = new string('x', 1 << 20) },
            TestContext.Current.CancellationToken);

        Assert.True(ran.Ok);
    }

    [Fact]
    public async Task Caller_cancellation_stops_the_child_and_propagates()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sh(1) is a Unix program.");
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "still-running");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _proc.RunAsync(new ProcRequest(["sh", "-c", $"sleep 1; touch '{marker}'"]), cancel.Token));

        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Records_a_span_per_call()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source == SystemProc.Source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(listener);

        await _proc.RunAsync(new ProcRequest(["git", "not-a-command"]), TestContext.Current.CancellationToken);
        await _proc.RunAsync(new ProcRequest(["git", "--version"]), TestContext.Current.CancellationToken);

        var mine = spans.Where(s => (s.GetTagItem("process.command_args") as string)?.StartsWith("git ", StringComparison.Ordinal) == true).ToList();
        Assert.Contains(mine, s => s.Status == ActivityStatusCode.Error && (int)s.GetTagItem("process.exit.code")! == 1);
        Assert.Contains(mine, s => s.Status == ActivityStatusCode.Unset && (int)s.GetTagItem("process.exit.code")! == 0);
    }
}
