using System.CommandLine;
using Rig.Cli.Status;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Status;

/// <summary>Ported from the Python <c>repo_health</c> entry point, which had no tests of its own.</summary>
public sealed class StatusCommandTests : IDisposable
{
    private const string Here = "/work/rig/src";
    private const string Root = "/work/rig";

    private static readonly Ran Fine = new(0, string.Empty, string.Empty);

    private readonly FakeProc _proc = new() { Unanswered = Fine };
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public StatusCommandTests()
    {
        _ = _proc.Answer(new Ran(0, $"{Root}\n", string.Empty), "git", "rev-parse", "--show-toplevel")
            .Answer(new Ran(0, "main\n", string.Empty), "git", "rev-parse", "--abbrev-ref", "HEAD")
            .Answer(new Ran(0, "0\t0\n", string.Empty), "git", "rev-list");
    }

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
    }

    private Task<int> Run(params string[] args) =>
        new StatusCommand(_proc, new Git(_proc, "/code"), Here, _out).Build().Parse(args)
            .InvokeAsync(new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);

    private ProcRequest[] Gates() => [.. _proc.Requests.Where(r => r.Argv[0] == "uv")];

    [Fact]
    public async Task A_clean_synced_repo_with_passing_gates_is_healthy()
    {
        Assert.Equal(0, await Run("status"));

        var text = _out.ToString();
        Assert.Contains("PASS  Healthy", text, StringComparison.Ordinal);
        Assert.Contains("11 checks passed", text, StringComparison.Ordinal);
        Assert.Contains("│ OK │ Branch", text, StringComparison.Ordinal);
        Assert.Contains("│ OK │ Security", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public async Task Every_gate_and_git_command_runs_at_the_repo_root()
    {
        _ = await Run("status");

        Assert.Equal(StatusCommand.Gates.Select(g => g.Argv), Gates().Select(r => r.Argv));
        Assert.All(Gates(), r => Assert.Equal(Root, r.WorkingDirectory));
        var git = _proc.Requests.Where(r => r.Argv[0] == "git").ToList();
        Assert.Equal(Here, git[0].WorkingDirectory);
        Assert.All(git.Skip(1), r => Assert.Equal(Root, r.WorkingDirectory));
    }

    [Fact]
    public async Task Outside_a_repo_the_working_directory_is_the_root()
    {
        _ = _proc.Answer(new Ran(128, string.Empty, "fatal: not a git repository"), "git", "rev-parse", "--show-toplevel");

        _ = await Run("status");

        Assert.All(Gates(), r => Assert.Equal(Here, r.WorkingDirectory));
    }

    [Fact]
    public async Task A_dirty_worktree_unsynced_branch_and_failing_gate_are_issues()
    {
        _ = _proc.Answer(new Ran(0, " M src/a.py\n?? b.txt\n", string.Empty), "git", "status", "--porcelain")
            .Answer(new Ran(0, "2\t1\n", string.Empty), "git", "rev-list")
            .Answer(new Ran(1, "src/a.py:1: E501\n", "Found 3 errors.\n\n"), "uv", "run", "ruff", "check");

        Assert.Equal(1, await Run("status"));

        var text = _out.ToString();
        Assert.Contains("FAIL  Issues Found", text, StringComparison.Ordinal);
        Assert.Contains("│ !! │ Clean worktree", text, StringComparison.Ordinal);
        Assert.Contains("M src/a.py", text, StringComparison.Ordinal);
        Assert.Contains("│ !! │ Remote sync", text, StringComparison.Ordinal);
        Assert.Contains("+2/-1", text, StringComparison.Ordinal);
        Assert.Contains("│ !! │ Lint", text, StringComparison.Ordinal);
        Assert.Contains("Found 3 errors.", text, StringComparison.Ordinal);
        Assert.Contains("│ OK │ Format", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_with_no_upstream_has_no_sync_line()
    {
        _ = _proc.Answer(new Ran(128, string.Empty, "fatal: no upstream configured for branch 'x'\n"), "git", "rev-list");

        Assert.Equal(0, await Run("status"));

        Assert.DoesNotContain("Remote sync", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_tool_fails_its_gate_with_what_it_said()
    {
        _ = _proc.Answer(new Ran(Ran.NotFound, string.Empty, "uv: not found"), "uv");

        Assert.Equal(1, await Run("status"));

        Assert.Contains("uv: not found", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("FAIL  Issues Found", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "status" }, null)]
    [InlineData(new[] { "status", "--tests" }, new[] { "uv", "run", "pytest", "-q", "--tb=no" })]
    [InlineData(new[] { "status", "-t" }, new[] { "uv", "run", "pytest", "-q", "--tb=no" })]
    [InlineData(new[] { "status", "--ci" }, new[] { "uv", "run", "pytest", "-q", "--tb=no", "-m", "ci" })]
    [InlineData(new[] { "status", "-t", "--ci" }, new[] { "uv", "run", "pytest", "-q", "--tb=no", "-m", "ci" })]
    public async Task Tests_run_only_when_asked(string[] args, string[]? pytest)
    {
        _ = _proc.Answer(new Ran(0, "....\n412 passed in 3.1s\n", string.Empty), "uv", "run", "pytest");

        _ = await Run(args);

        var ran = _proc.Requests.Where(r => r.Argv.Contains("pytest")).Select(r => r.Argv).SingleOrDefault();
        Assert.Equal(pytest, ran);
        if (pytest is not null)
        {
            Assert.Contains("│ OK │ Tests", _out.ToString(), StringComparison.Ordinal);
            Assert.Contains("412 passed in 3.1s", _out.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Failing_tests_are_an_issue()
    {
        _ = _proc.Answer(new Ran(1, "F.\n1 failed, 1 passed\n", string.Empty), "uv", "run", "pytest");

        Assert.Equal(1, await Run("status", "--tests"));

        Assert.Contains("│ !! │ Tests", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("1 failed, 1 passed", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_plain_render_form_is_machine_safe_and_names_every_check()
    {
        var text = StatusCommand.Render(
        [
            new StatusResult("Branch", true, "main"),
            new StatusResult("Lint", false, "src/a.py:1: E501"),
        ]);

        Assert.StartsWith("Issues Found\n", text, StringComparison.Ordinal);
        Assert.Contains("Branch", text, StringComparison.Ordinal);
        Assert.Contains("E501", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public void The_detail_is_the_last_non_blank_line_cut_to_100_characters()
    {
        Assert.Equal(string.Empty, StatusCommand.LastLine(string.Empty));
        Assert.Equal("b", StatusCommand.LastLine("a\nb\n  \n"));
        Assert.Equal(new string('x', 100), StatusCommand.LastLine(new string('x', 140)));
    }
}
