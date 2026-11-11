using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Workspace;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Workspace;

/// <summary>
/// <c>rig workspace doctor</c> over a temp code root of clones, a fake git and gh, and a temp home for the name
/// cache. Ported from <c>tests/test_workspace_doctor_cli.py</c>.
/// </summary>
public sealed class WorkspaceCommandTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public WorkspaceCommandTests()
    {
        var scopes = _root.File(Path.Join("rig", "config", EstateScopes.FileName));
        File.WriteAllText(scopes, """{"personal": {"owners": ["rtkelly13"], "orgs": ["labs"]}}""");
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Personal => Path.Join(_root.Path, "personal");

    private void Clone(string parent, string name, string remote)
    {
        var folder = Path.Join(_root.Path, parent, name);
        _ = Directory.CreateDirectory(Path.Join(folder, ".git"));
        _ = _proc
            .AnswerIn(folder, new(0, ".git\n", string.Empty), "git", "rev-parse")
            .AnswerIn(folder, new(0, $"git@github.com:{remote}.git\n", string.Empty), "git", "config");
    }

    // GitHub knows each repository by the given name, in the order the scan asks (folders sorted ignoring case).
    private void GitHubNames(params string[] names) =>
        _proc.Answer(
            new(0, new JsonObject
            {
                ["data"] = new JsonObject(names.Select((name, i) => KeyValuePair.Create<string, JsonNode?>(
                    $"r{i}",
                    name.Length == 0 ? null : new JsonObject { ["name"] = name, ["nameWithOwner"] = $"o/{name}", ["isArchived"] = false, ["url"] = string.Empty }))),
            }.ToJsonString(), string.Empty),
            "gh",
            "api");

    private async Task<(int Code, string Out)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        var host = new WorkspaceHost(Personal, Path.Join(_root.Path, "rig", "config", EstateScopes.FileName), Path.Join(_root.Path, "home"));
        _ = Directory.CreateDirectory(host.Home);
        var root = new RootCommand { new WorkspaceCommand(host, _proc, TimeProvider.System, output).Build() };
        var code = await root.Parse(["workspace", "doctor", .. args]).InvokeAsync(new InvocationConfiguration { Output = output, Error = output }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_clean_estate_and_its_org_folders_are_reported_and_exit_0_even_when_strict()
    {
        Clone("personal", "rig", "rtkelly13/rig");
        Clone("labs", "lake", "labs/lake");
        GitHubNames("lake", "rig");

        var (code, output) = await RunAsync("--strict");

        Assert.Equal(0, code);
        Assert.StartsWith($"Estate Repository Doctor: {Personal}\nFolder  Canonical Upstream  Remote Slug    Status  Detail\n", output, StringComparison.Ordinal);
        Assert.Contains("lake    lake                labs/lake      ✓ OK    Folder and remote match canonical name 'lake'\n", output, StringComparison.Ordinal);
        Assert.EndsWith("✓ All estate repository folders match their canonical upstream names.\n\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mismatches_are_labelled_and_fail_only_when_strict()
    {
        Clone("personal", "a-old", "rtkelly13/a-old");
        Clone("personal", "b", "rtkelly13/b2");
        Clone("personal", "c", "rtkelly13/c-old");
        Clone("personal", "d", "rtkelly13/d");
        Clone("personal", "e", "rtkelly13/e");
        GitHubNames("a", "b2", "c", "", "e");

        var (lax, output) = await RunAsync();
        var (strict, _) = await RunAsync("--strict", "--refresh-cache");

        Assert.Equal(0, lax);
        Assert.Equal(1, strict);
        Assert.Contains("! Renamed (Outdated)", output, StringComparison.Ordinal);
        Assert.Contains("✗ Mismatch", output, StringComparison.Ordinal);
        Assert.Contains("! Remote Outdated", output, StringComparison.Ordinal);
        Assert.Contains("? Unresolved", output, StringComparison.Ordinal);
        Assert.EndsWith("! Repository folder or remote mismatches detected. Review the report above.\n\n", output, StringComparison.Ordinal);
        Assert.Equal(2, _proc.Requests.Count(r => r.Argv[0] == "gh"));
    }

    [Fact]
    public async Task The_json_report_keeps_the_python_fields()
    {
        Clone("personal", "shared-utilities", "rtkelly13/shared-utilities");
        GitHubNames("rig");

        var (code, output) = await RunAsync("--json", "--strict");

        Assert.Equal(1, code);
        var report = JsonNode.Parse(output)!;
        Assert.Equal(Personal, (string?)report["estate_root"]);
        Assert.False((bool)report["is_clean"]!);
        Assert.Equal(1, (int)report["total_repos"]!);
        var check = report["checks"]![0]!;
        Assert.Equal(
            """{"folder_name":"shared-utilities","canonical_name":"rig","owner":"rtkelly13","remote_repo_name":"shared-utilities","status":"folder_renamed","is_match":false,"remote_matches_canonical":false,"is_renamed":true,"detail":"Folder 'shared-utilities' is named after a previous repository name; renamed upstream to 'rig'"}""",
            check.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Contains("\n  \"estate_root\": ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_estate_root_is_clean_and_asks_nothing()
    {
        var (code, output) = await RunAsync("--root", Path.Join(_root.Path, "nowhere"), "--json");

        Assert.Equal(0, code);
        Assert.Equal(0, (int)JsonNode.Parse(output)!["total_repos"]!);
        Assert.Empty(_proc.Requests);
    }
}
