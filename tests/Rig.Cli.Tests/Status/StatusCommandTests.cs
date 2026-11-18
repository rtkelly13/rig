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

    private ProcRequest[] Dotnet() => [.. _proc.Requests.Where(r => r.Argv[0] == "dotnet")];

    [Fact]
    public async Task A_clean_synced_repo_with_passing_gates_is_healthy()
    {
        Assert.Equal(0, await Run("status"));

        var text = _out.ToString();
        Assert.Contains("PASS  Healthy", text, StringComparison.Ordinal);
        Assert.Contains("3 checks passed", text, StringComparison.Ordinal);
        Assert.Contains("│ OK │ Branch", text, StringComparison.Ordinal);
        Assert.Contains("│ OK │ Remote sync", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);
    }

    [Fact]
    public async Task Git_commands_run_where_asked_and_nothing_else_runs_without_tests()
    {
        _ = await Run("status");

        Assert.Empty(Dotnet());
        var git = _proc.Requests.Where(r => r.Argv[0] == "git").ToList();
        Assert.Equal(Here, git[0].WorkingDirectory);
        Assert.All(git.Skip(1), r => Assert.Equal(Root, r.WorkingDirectory));
    }

    [Fact]
    public async Task Outside_a_repo_the_working_directory_is_the_root()
    {
        _ = _proc.Answer(new Ran(128, string.Empty, "fatal: not a git repository"), "git", "rev-parse", "--show-toplevel");

        _ = await Run("status", "--tests");

        Assert.All(Dotnet(), r => Assert.Equal(Here, r.WorkingDirectory));
    }

    [Fact]
    public async Task A_dirty_worktree_unsynced_branch_and_failing_gate_are_issues()
    {
        _ = _proc.Answer(new Ran(0, " M src/a.cs\n?? b.txt\n", string.Empty), "git", "status", "--porcelain")
            .Answer(new Ran(0, "2\t1\n", string.Empty), "git", "rev-list");

        Assert.Equal(1, await Run("status"));

        var text = _out.ToString();
        Assert.Contains("FAIL  Issues Found", text, StringComparison.Ordinal);
        Assert.Contains("│ !! │ Clean worktree", text, StringComparison.Ordinal);
        Assert.Contains("M src/a.cs", text, StringComparison.Ordinal);
        Assert.Contains("│ !! │ Remote sync", text, StringComparison.Ordinal);
        Assert.Contains("+2/-1", text, StringComparison.Ordinal);
        Assert.Contains("│ OK │ Branch", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_with_no_upstream_has_no_sync_line()
    {
        _ = _proc.Answer(new Ran(128, string.Empty, "fatal: no upstream configured for branch 'x'\n"), "git", "rev-list");

        Assert.Equal(0, await Run("status"));

        Assert.DoesNotContain("Remote sync", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_tool_fails_the_tests_check_with_what_it_said()
    {
        _ = _proc.Answer(new Ran(Ran.NotFound, string.Empty, "dotnet: not found"), "dotnet");

        Assert.Equal(1, await Run("status", "--tests"));

        Assert.Contains("dotnet: not found", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("FAIL  Issues Found", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--tests")]
    [InlineData("-t")]
    public async Task Tests_run_when_asked(string flag)
    {
        _ = _proc.Answer(new Ran(0, "Passed! - Failed: 0, Passed: 412\n", string.Empty), "dotnet", "test");

        _ = await Run("status", flag);

        Assert.Equal(["dotnet", "test", "--solution", "rig.slnx"], Assert.Single(Dotnet()).Argv);
        Assert.Contains("│ OK │ Tests", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Passed: 412", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_tests_are_an_issue()
    {
        _ = _proc.Answer(new Ran(1, "F.\n1 failed, 1 passed\n", string.Empty), "dotnet", "test");

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
            new StatusResult("Tests", false, "1 failed"),
        ]);

        Assert.StartsWith("Issues Found\n", text, StringComparison.Ordinal);
        Assert.Contains("Branch", text, StringComparison.Ordinal);
        Assert.Contains("1 failed", text, StringComparison.Ordinal);
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
