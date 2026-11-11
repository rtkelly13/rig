using System.CommandLine;
using Rig.Cli.Workspace;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Cli.Tests.Workspace;

/// <summary><c>rig workspace ci audit</c> over a temp registry and a fake <c>gh api</c>.</summary>
public sealed class WorkspaceCiCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public WorkspaceCiCommandTests() =>
        File.WriteAllText(_root.File(Path.Join("rig", "config", ManagedProjects.FileName)), """
            {"projects": [
              {"name": "app", "repo": "git@github.com:o/app.git"},
              {"name": "tool", "repo": "https://github.com/labs/tool"},
              {"name": "scratch", "repo": "local/scratch"}
            ]}
            """);

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var command = new WorkspaceCommand(
            new WorkspaceHost(
                Path.Join(_root.Path, "personal"),
                Path.Join(_root.Path, "rig", "config", "estate-scopes.json"),
                Path.Join(_root.Path, "home"),
                Path.Join(_root.Path, "rig", "config", ManagedProjects.FileName),
                Path.Join(_root.Path, "home", ".workspace", "config.json")),
            _proc,
            TimeProvider.System,
            output);
        var code = await new RootCommand { command.Build() }.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    private FakeProc Gh(string route, string json) => _proc.Answer(new Ran(0, json, string.Empty), "gh", "api", route);

    [Fact]
    public async Task Each_run_is_a_row_and_the_billable_minutes_are_totalled()
    {
        _ = Gh("repos/o/app/actions/runs?per_page=10", """
            {"workflow_runs": [{"id": 7, "name": "CI", "event": "pull_request", "head_branch": "a-very-long-branch-name-indeed",
              "conclusion": "success", "run_started_at": "2026-10-10T12:00:00Z", "updated_at": "2026-10-10T12:03:05Z"}]}
            """);
        _ = Gh("repos/o/app/actions/runs/7/jobs", """
            {"jobs": [{"id": 1, "name": "test-windows", "started_at": "2026-10-10T12:00:00Z", "completed_at": "2026-10-10T12:01:30Z"}]}
            """);

        var (code, output) = await RunAsync("workspace", "ci", "audit");

        Assert.Equal(0, code);
        Assert.Equal(
            """
            Estate CI Utilisation Audit
            Repository  Workflow  Event / Branch                     Conclusion  Wall Time  Billable Equiv.
            app         CI        pull_request (a-very-long-branch)  success         3m 5s            3m 0s

            Total Evaluated: 1 runs | Total Billable Minutes Equivalent: 3.0m


            """,
            output);
    }

    [Fact]
    public async Task Repo_and_limit_narrow_the_audit_and_a_local_project_is_skipped()
    {
        var (code, output) = await RunAsync("workspace", "ci", "audit", "-r", "LABS/TOOL", "-l", "3");

        Assert.Equal(0, code);
        Assert.Contains("Total Evaluated: 0 runs | Total Billable Minutes Equivalent: 0.0m", output, StringComparison.Ordinal);
        Assert.Equal(["gh", "api", "repos/labs/tool/actions/runs?per_page=3"], Assert.Single(_proc.Requests).Argv);
    }

    [Fact]
    public async Task An_unknown_repo_fails()
    {
        var (code, output) = await RunAsync("workspace", "ci", "audit", "--repo", "nope");

        Assert.Equal(1, code);
        Assert.Equal("Error: repository 'nope' not found in managed projects.\n", output);
        Assert.Empty(_proc.Requests);
    }
}
