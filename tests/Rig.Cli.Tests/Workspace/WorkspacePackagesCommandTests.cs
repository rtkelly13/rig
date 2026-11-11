using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Workspace;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Cli.Tests.Workspace;

/// <summary><c>rig workspace packages</c> over a temp registry, a fake registry and fake <c>gh</c> and <c>git</c>.</summary>
public sealed class WorkspacePackagesCommandTests : IDisposable
{
    private const string Packument = "https://registry.npmjs.org/@s%2fpkg";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();
    private readonly FakeHttp _http = new();

    public WorkspacePackagesCommandTests()
    {
        File.WriteAllText(_root.File(Path.Join("rig", "config", ManagedProjects.FileName)), """
            {"projects": [
              {"name": "app", "repo": "git@github.com:o/app.git",
               "packages": [{"registry": "npm", "name": "@s/pkg", "manifest": "package.json", "workflow": "publish.yml"}]},
              {"name": "other", "repo": "git@github.com:o/other.git",
               "packages": [{"registry": "nuget", "name": "Other.Pkg", "manifest": "Other.csproj", "workflow": "release.yml"}]}
            ]}
            """);
        File.WriteAllText(_root.File(Path.Join("personal", "app", "package.json")), """{"name": "@s/pkg", "version": "1.1.0"}""");
        _http.Routes[Packument] = """
            {"dist-tags": {"latest": "1.0.0"},
             "time": {"1.0.0": "2026-09-25T12:00:00Z"},
             "versions": {"1.0.0": {}}}
            """;
    }

    public void Dispose()
    {
        _http.Dispose();
        _root.Dispose();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var http = _http.Client();
        var command = new WorkspaceCommand(
            new WorkspaceHost(
                Path.Join(_root.Path, "personal"),
                Path.Join(_root.Path, "rig", "config", "estate-scopes.json"),
                Path.Join(_root.Path, "home"),
                Path.Join(_root.Path, "rig", "config", ManagedProjects.FileName),
                Path.Join(_root.Path, "home", ".workspace", "config.json")),
            _proc,
            TimeProvider.System,
            output,
            http);
        var code = await new RootCommand { command.Build() }.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Status_reads_the_local_manifest_against_the_registry()
    {
        var (code, output) = await RunAsync("workspace", "packages", "status", "--project", "APP", "--json");

        Assert.Equal(0, code);
        var record = Assert.Single(JsonNode.Parse(output)!.AsArray())!;
        Assert.Equal(
            ("@s/pkg", "1.1.0", "1.0.0", "1.0.0", "unreleased", "2026-09-25T12:00:00+00:00"),
            ((string?)record["package"], (string?)record["local_version"], (string?)record["latest_version"],
             (string?)record["deployed_version"], (string?)record["status"], (string?)record["published_at"]));
    }

    [Fact]
    public async Task The_artifacts_alias_runs_status_as_a_table()
    {
        var (code, output) = await RunAsync("workspace", "artifacts");

        Assert.Equal(0, code);
        Assert.Contains("Deployed Packages & Artifacts (2)", output, StringComparison.Ordinal);
        Assert.Contains("2026-09-25 (", output, StringComparison.Ordinal);
        Assert.Contains("unpublished", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_fails_under_strict_when_a_repo_cannot_be_resolved()
    {
        var (lenient, _) = await RunAsync("workspace", "packages", "audit", "-p", "app");
        var (strict, output) = await RunAsync("workspace", "packages", "audit", "-p", "app", "--strict", "--json");

        Assert.Equal((0, 1), (lenient, strict));
        var findings = JsonNode.Parse(output)!.AsArray();
        Assert.Contains(findings, f => (string?)f!["check"] == "repo" && (string?)f["status"] == "fail");
    }

    [Fact]
    public async Task Audit_prints_one_table_and_the_trust_hints()
    {
        var (code, output) = await RunAsync("workspace", "packages", "audit");

        Assert.Equal(0, code);
        Assert.Contains("Published packages (2)", output, StringComparison.Ordinal);
        Assert.Contains("npm trust configs not read", output, StringComparison.Ordinal);
        Assert.Contains("NuGet policies have no API", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_matches_registry_versions_against_the_clones_tags()
    {
        _ = _proc.Answer(new Ran(0, "v1.0.0\n", string.Empty), "git", "tag", "-l");

        var (code, output) = await RunAsync("workspace", "packages", "dashboard", "--json");

        Assert.Equal(0, code);
        var rows = JsonNode.Parse(output)!.AsArray();
        Assert.Equal(
            [("@s/pkg", "1.0.0", "v1.0.0", "synced"), ("Other.Pkg", null, null, "unpublished")],
            rows.Select(r => ((string?)r!["package"], (string?)r["registry_version"], (string?)r["latest_tag"], (string?)r["status"])));
    }

    [Fact]
    public async Task Dashboard_summarises_the_rows()
    {
        var (code, output) = await RunAsync("workspace", "packages", "dashboard", "--root", "~/nowhere");

        Assert.Equal(0, code);
        Assert.Contains("2 packages · 0 synced · 1 flagged · 1 unpublished", output, StringComparison.Ordinal);
        Assert.Contains("tags unavailable", output, StringComparison.Ordinal);
    }
}
