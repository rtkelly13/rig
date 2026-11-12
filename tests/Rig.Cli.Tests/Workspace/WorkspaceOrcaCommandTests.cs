using System.CommandLine;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Rig.Cli.Workspace;
using Rig.Core.Orca;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Workspace;

namespace Rig.Cli.Tests.Workspace;

/// <summary>
/// <c>rig workspace orca</c> over a temp registry and policy, a fake <c>orca</c> CLI and a fake runtime socket. Nothing here
/// reaches a real Orca: the instance is always <c>--orca-data</c> under the temp root, and the environment is empty.
/// </summary>
public sealed class WorkspaceOrcaCommandTests : IDisposable
{
    private const string RepoList = """{"result": {"repos": [{"path": "/elsewhere/app", "displayName": "app", "badgeColor": "#737373"}]}}""";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();
    private readonly FakeOrca _orca = new();

    public WorkspaceOrcaCommandTests()
    {
        var config = Path.Join(_root.Path, "rig", "config");
        File.WriteAllText(_root.File(Path.Join("rig", "config", ManagedProjects.FileName)), """
            {"projects": [{"name": "app", "repo": "git@github.com:o/app.git"}]}
            """);
        File.WriteAllText(Path.Join(config, ManagedProjects.ArchivedFileName), """
            {"projects": [{"name": "old", "repo": "git@github.com:o/old.git"}]}
            """);
        File.WriteAllText(Path.Join(config, "orca.json"), """
            {"agent_permissions": {"default": "own", "agents": {"claude": "auto"}},
             "repos": {"app": {"color": "#6366f1"}, "old": {}, "fresh": {}}}
            """);
    }

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string OrcaData => Path.Join(_root.Path, "orca");

    private void OrcaRunning() =>
        File.WriteAllText(_root.File(Path.Join("orca", OrcaLocations.RuntimeFile)), """
            {"authToken": "t", "transports": [{"kind": "unix", "endpoint": "/never/dialled.sock"}]}
            """);

    private FakeProc Repos(string json) => _proc.Answer(new Ran(0, json, string.Empty), "orca", "repo", "list");

    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var http = new HttpClient();
        var locations = new OrcaLocations(_ => null, Path.Join(_root.Path, "home"), OrcaOs.Linux);
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
            http,
            new OrcaHost(locations, _orca, error));
        // automations names its instance in the manifest, so it takes no --orca-data.
        string[] full = args.Contains("automations") ? args : [.. args, "--orca-data", OrcaData];
        var code = await new RootCommand { command.Build() }.Parse(full).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Sync_exits_3_when_Orca_is_not_running()
    {
        var (code, output, error) = await RunAsync("workspace", "orca", "sync");

        Assert.Equal((OrcaCommand.Unavailable, string.Empty), (code, output));
        Assert.Contains("Orca is not running", error, StringComparison.Ordinal);
        Assert.Empty(_orca.Methods);
    }

    [Fact]
    public async Task A_dry_run_plans_launch_lines_registration_and_a_retired_project_as_manual()
    {
        OrcaRunning();
        _ = Repos(RepoList);
        _ = _root.Folder(Path.Join("personal", "fresh"));

        var (code, output, _) = await RunAsync("workspace", "orca", "sync", "--json");

        Assert.Equal(0, code);
        var rows = JsonNode.Parse(output)!.AsArray().Select(row => row!.AsObject()).ToList();
        var launch = Assert.Single(rows, row => (string?)row["repo"] == "agent claude");
        Assert.Equal(("update", "launch args", false, "planned"), ((string?)launch["action"], (string?)launch["field"], (bool?)launch["applied"], (string?)launch["detail"]));
        Assert.Equal("manual", (string?)Assert.Single(rows, row => (string?)row["repo"] == "old")["action"]);
        Assert.Equal("register", (string?)Assert.Single(rows, row => (string?)row["repo"] == "fresh")["action"]);
        Assert.Contains(rows, row => (string?)row["repo"] == "app" && (string?)row["new_value"] == "#6366f1");
        Assert.Equal(["settings.get"], _orca.Methods);
        Assert.Equal(OrcaData, _proc.Requests[0].Environment[OrcaLocations.UserDataVariable]);
    }

    [Fact]
    public async Task Apply_sends_the_launch_lines_and_reports_them_applied()
    {
        OrcaRunning();
        _ = Repos(RepoList);

        var (code, output, _) = await RunAsync("workspace", "orca", "sync", "--apply", "--repo", "nothing-like-it");
        Assert.Equal((0, "All Orca repositories are in sync with policy.\n"), (code, output));

        (code, output, _) = await RunAsync("workspace", "orca", "sync", "--apply", "--repo", "app");
        Assert.Equal(0, code);
        Assert.Contains("APPLIED", output, StringComparison.Ordinal);
        Assert.Contains("repo.update", _orca.Methods);
        Assert.DoesNotContain("settings.update", _orca.Methods);

        (_, output, _) = await RunAsync("workspace", "orca", "sync", "--apply", "--json");
        var launch = JsonNode.Parse(output)!.AsArray().Single(row => (string?)row!["repo"] == "agent claude")!;
        Assert.True((bool?)launch["applied"]);
        Assert.Contains("settings.update", _orca.Methods);
    }

    [Fact]
    public async Task A_repo_list_that_fails_is_an_error_row_and_exit_1()
    {
        OrcaRunning();

        var (code, output, _) = await RunAsync("workspace", "orca", "sync");

        Assert.Equal(1, code);
        Assert.Contains("DRY-RUN (pass --apply to execute)", output, StringComparison.Ordinal);
        Assert.Contains("Orca repos could not be listed", output, StringComparison.Ordinal);
        Assert.Contains("(none)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_reports_each_finding_and_fails_on_any_failure()
    {
        _ = Repos(RepoList);

        var (code, output, _) = await RunAsync("workspace", "orca", "doctor", "--json");

        var rows = JsonNode.Parse(output)!.AsArray().Select(row => row!.AsObject()).ToList();
        Assert.Contains(rows, row => (string?)row["subject"] == "agents" && (string?)row["status"] == "skip");
        Assert.All(rows, row => Assert.Equal(["subject", "check", "status", "detail", "fix"], row.Select(pair => pair.Key)));
        Assert.Equal(rows.Any(row => (string?)row["status"] == "fail") ? 1 : 0, code);

        (_, output, _) = await RunAsync("workspace", "orca", "doctor");
        Assert.StartsWith("Orca vs orca.json\nSubject", output, StringComparison.Ordinal);
        Assert.Contains("fix: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_policy_is_read_instead_of_the_checkouts()
    {
        var policy = _root.File("elsewhere.json");
        await File.WriteAllTextAsync(policy, """{"repos": {}}""", Token);
        _ = Repos("""{"result": {"repos": []}}""");

        var (code, output, _) = await RunAsync("workspace", "orca", "doctor", "--config", policy);

        Assert.Equal(0, code);
        Assert.StartsWith("Orca vs elsewhere.json", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Automations_reconcile_the_manifest_in_the_instance_this_machine_maps_and_skip_other_roles()
    {
        await File.WriteAllTextAsync(_root.File(Path.Join("rig", "config", AutomationManifest.FileName)), """
            {"schema_version": 2, "automations": [
              {"key": "rev", "name": "Review", "roles": ["personal"], "orca": {"instance": "personal"},
               "trigger": {"rrule": "FREQ=WEEKLY;BYDAY=MO", "timezone": "Europe/London"}, "provider": {"agent": "codex"},
               "workspace": {"path": "{personal}/rig"}, "bootstrap": "docs/b.md", "policy_path": "docs/rev.md"},
              {"key": "job", "name": "Work job", "roles": ["work"], "orca": {"instance": "work"},
               "trigger": {"rrule": "FREQ=DAILY", "timezone": "Europe/London"}, "provider": {"agent": "claude"},
               "workspace": {"path": "/w"}, "bootstrap": "docs/b.md", "policy_path": "docs/job.md"}]}
            """, Token);
        await File.WriteAllTextAsync(_root.File(Path.Join("rig", "docs", "b.md")), "```text\nRun {{key}} from {{policy_path}}.\n```\n", Token);
        await File.WriteAllTextAsync(_root.File(Path.Join("home", ".workspace", "config.json")), """
            {"tools": {"machine": {"role": "personal"}, "orca": {"instances": {"personal": "~/orca-p"}}}}
            """, Token);
        _ = _proc.Answer(new Ran(0, """{"result": {"automations": []}}""", string.Empty), "orca", "automations", "list");
        _ = _proc.Answer(new Ran(0, """{"result": {"automation": {"id": "made"}}}""", string.Empty), "orca", "automations", "create");

        var (code, output, _) = await RunAsync("workspace", "orca", "automations", "--json");

        Assert.Equal(0, code);
        var payload = JsonNode.Parse(output)!;
        Assert.Equal("personal", (string?)payload["role"]);
        Assert.Equal(
            [("rev", "create"), ("job", "skip")],
            payload["changes"]!.AsArray().Select(c => ((string)c!["key"]!, (string)c["action"]!)));
        var list = Assert.Single(_proc.Requests);
        Assert.Equal(Path.Join(_root.Path, "home", "orca-p"), list.Environment[OrcaLocations.UserDataVariable]);

        (code, output, _) = await RunAsync("workspace", "orca", "automations", "--apply", "--key", "rev");

        Assert.Equal(0, code);
        Assert.Contains("create (applied)", output, StringComparison.Ordinal);
        var create = _proc.Requests[^1].Argv;
        Assert.Equal("Run rev from docs/rev.md.", create[create.ToList().IndexOf("--prompt") + 1]);
        Assert.Equal("path:" + Path.Join(_root.Path, "personal").Replace('\\', '/') + "/rig", create[create.ToList().IndexOf("--workspace") + 1]);
    }

    [Fact]
    public async Task Automations_with_an_unknown_key_or_no_manifest_exit_2()
    {
        var (code, _, error) = await RunAsync("workspace", "orca", "automations");
        Assert.Equal(2, code);
        Assert.Contains("Cannot read", error, StringComparison.Ordinal);

        await File.WriteAllTextAsync(_root.File(Path.Join("rig", "config", AutomationManifest.FileName)), """{"schema_version": 2, "automations": []}""", Token);
        (code, _, error) = await RunAsync("workspace", "orca", "automations", "--key", "nope");
        Assert.Equal(2, code);
        Assert.Contains("No automation with key \"nope\"", error, StringComparison.Ordinal);
    }

    /// <summary>A running Orca's runtime: answers <c>settings.get</c>, <c>settings.update</c> and <c>repo.update</c> from its own state.</summary>
    private sealed class FakeOrca : IOrcaExchange
    {
        private JsonObject _launch = new() { ["claude"] = string.Empty };

        public List<string> Methods { get; } = [];

        public async IAsyncEnumerable<string> ExchangeAsync(OrcaTransport transport, string request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            var frame = JsonNode.Parse(request)!.AsObject();
            var method = (string)frame["method"]!;
            Methods.Add(method);
            if (method == "settings.update")
            {
                _launch = frame["params"]!["agentDefaultArgs"]!.DeepClone().AsObject();
            }

            JsonNode result = method == "repo.update" ? new JsonObject() : new JsonObject { ["settings"] = new JsonObject { ["agentDefaultArgs"] = _launch.DeepClone() } };
            yield return new JsonObject { ["id"] = frame["id"]!.DeepClone(), ["ok"] = true, ["result"] = result }.ToJsonString();
        }
    }
}
