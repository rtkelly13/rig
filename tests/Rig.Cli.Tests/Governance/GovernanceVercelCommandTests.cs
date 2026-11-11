using System.CommandLine;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance vercel-gating</c> and <c>vercel-projects</c> end to end, with Vercel, GitHub and git faked:
/// no real token is read and no real API is called.
/// </summary>
public sealed class GovernanceVercelCommandTests : IDisposable
{
    private const long Now = 1_800_000_000_000;

    private const string Projects = """
        {"projects": [
          {"id": "prj_1", "name": "site", "link": {"org": "acme", "repo": "site"}, "gitProviderOptions": {"createDeployments": "enabled"}},
          {"id": "prj_2", "name": "docs", "link": {"org": "acme", "repo": "docs"}, "gitProviderOptions": {"createDeployments": "disabled"}}
        ]}
        """;

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();
    private readonly FakeVercel _vercel = new();
    private readonly Dictionary<string, string> _environment = [];

    public GovernanceVercelCommandTests()
    {
        File.WriteAllText(_root.File($"checkout/config/{RepoPolicies.FileName}"), """{"defaults": {"vercel": {"team_id": "team_x", "window_days": 2}}}""");
        File.WriteAllText(
            _root.File("checkout/config/managed-projects.json"),
            """{"projects": [{"name": "site", "repo": "git@github.com:acme/site.git", "vercel": ["site"]}, {"name": "docs", "repo": "git@github.com:acme/docs.git"}]}""");
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Gating_tabulates_every_project_and_lists_its_findings()
    {
        _ = _vercel.On("/v9/projects", Projects).On("/v6/deployments", """{"deployments": []}""");

        var (code, output) = await RunAsync("vercel-gating", "--all");

        Assert.Equal(1, code);
        Assert.Contains("Vercel deployment-creation gating (window: 2d)", output, StringComparison.Ordinal);
        Assert.Contains("all branches", output, StringComparison.Ordinal);
        Assert.Contains("✓ gated", output, StringComparison.Ordinal);
        Assert.Contains("  creation-gate site: ", output, StringComparison.Ordinal);
        Assert.All(_vercel.Calls, call => Assert.Contains(new KeyValuePair<string, string>("teamId", "team_x"), call.Query));
    }

    [Fact]
    public async Task Days_override_the_configured_window()
    {
        _ = _vercel.On("/v9/projects", """{"projects": [{"id": "prj_2", "name": "docs", "link": {"repo": "docs"}, "gitProviderOptions": {"createDeployments": "disabled"}}]}""")
            .On("/v6/deployments", """{"deployments": []}""");

        var (code, output) = await RunAsync("vercel-gating", "--all", "--days", "7");

        Assert.Equal(0, code);
        Assert.Contains("(window: 7d)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_linked_projects_is_reported_and_passes()
    {
        _ = _vercel.On("/v9/projects", """{"projects": [{"id": "prj_9", "name": "cli-only"}]}""");

        Assert.Equal((0, "No linked Vercel projects for the team\n"), await RunAsync("vercel-gating", "--all"));
    }

    [Fact]
    public async Task A_path_with_no_repository_is_refused()
    {
        var (code, output) = await RunAsync("vercel-gating", Path.Join(_root.Path, "nowhere"));

        Assert.Equal(2, code);
        Assert.StartsWith("❌ No repository found at ", output, StringComparison.Ordinal);
        Assert.Empty(_vercel.Calls);
    }

    [Theory]
    [InlineData("vercel-gating", "--all")]
    [InlineData("vercel-projects")]
    public async Task Without_a_team_nothing_is_called(params string[] args)
    {
        await File.WriteAllTextAsync(Path.Join(_root.Path, "checkout", "config", RepoPolicies.FileName), "{}", Token);

        Assert.Equal((2, "❌ No Vercel team id: set defaults.vercel.team_id or VERCEL_TEAM_ID\n"), await RunAsync(args));
        Assert.Empty(_vercel.Calls);
    }

    [Fact]
    public async Task The_team_can_come_from_the_environment()
    {
        await File.WriteAllTextAsync(Path.Join(_root.Path, "checkout", "config", RepoPolicies.FileName), "{}", Token);
        _environment["VERCEL_TEAM_ID"] = "team_env";
        _ = _vercel.On("/v9/projects", """{"projects": []}""");

        _ = await RunAsync("vercel-gating", "--all");

        Assert.Contains(new KeyValuePair<string, string>("teamId", "team_env"), Assert.Single(_vercel.Calls).Query);
    }

    [Theory]
    [InlineData("vercel-gating", "--all")]
    [InlineData("vercel-projects")]
    public async Task A_vercel_failure_is_reported_not_thrown(params string[] args) =>
        Assert.Equal((2, "❌ Vercel /v9/projects answered HTTP 404\n"), await RunAsync(args));

    [Fact]
    public async Task Projects_tabulates_ownership_and_fails_on_an_undeclared_project()
    {
        _ = _vercel.On("/v9/projects", Projects);

        var (code, output) = await RunAsync("vercel-projects");

        Assert.Equal(1, code);
        Assert.Contains("Vercel project ownership", output, StringComparison.Ordinal);
        Assert.Contains("deploys from docs (its git link): declare it under docs", output, StringComparison.Ordinal);
        Assert.Contains("ok", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Projects_passes_when_every_project_is_declared()
    {
        _ = _vercel.On("/v9/projects", """{"projects": [{"id": "prj_1", "name": "site", "link": {"org": "acme", "repo": "site"}}]}""");

        var (code, output) = await RunAsync("vercel-projects");

        Assert.Equal(0, code);
        Assert.DoesNotContain("—", output.Split('\n').Last(line => line.Contains("site", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_registry_is_reported_not_thrown()
    {
        await File.WriteAllTextAsync(Path.Join(_root.Path, "checkout", "config", "managed-projects.json"), """{"projects": "nope"}""", Token);

        var (code, output) = await RunAsync("vercel-projects");

        Assert.Equal(2, code);
        Assert.StartsWith("❌ ", output, StringComparison.Ordinal);
    }

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(
            new GovernanceHost(_root.Path, _root.Path, Path.Join(_root.Path, "checkout")),
            _proc,
            new FixedTime(DateTimeOffset.FromUnixTimeMilliseconds(Now)),
            output,
            new FakeGitHub(),
            vercel: _vercel,
            environment: name => _environment.GetValueOrDefault(name));
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
