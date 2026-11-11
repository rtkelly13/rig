using System.CommandLine;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Rig.Cli.Estate;
using Rig.Cli.Workspace;
using Rig.Core.Estate;
using Rig.Core.Orca;
using Rig.Core.Roots;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Orca;

namespace Rig.Cli.Tests.Estate;

/// <summary>
/// <c>rig estate orca</c> and <c>rig estate orca-worktrees</c> end to end. Each Orca profile is a <see cref="FakeOrca"/>
/// behind a runtime file in a temp folder, GitHub is faked, and the environment is empty, so no real Orca, socket or
/// API is reached. Ported from the command tests in <c>tests/test_estate_orca.py</c>.
/// </summary>
public sealed class OrcaCommandsTests : IDisposable
{
    private static readonly (string Name, string Owner, string Group)[] Layout =
    [
        ("rig", "rtkelly13", string.Empty),
        ("blobflags", "rtkelly13", "personal/tooling"),
        ("Iceberg.NET", "rtkelly-labs", "rtkelly-labs"),
        ("Diffra", "rtkelly-labs", "rtkelly-labs"),
    ];

    private readonly TempDir _temp = new();
    private readonly string _root;
    private readonly string _code;
    private readonly Profiles _profiles = new();
    private readonly FakeProc _proc = new();

    // GitHub reports none of the fixture repos archived; one of the real ones is.
    private readonly FakeGitHub _github = new FakeGitHub().On("POST", "graphql", """{"data": {}}""");

    public OrcaCommandsTests()
    {
        _root = PathResolution.Resolve(_temp.Path);
        _code = Path.Join(_root, "code");
        foreach (var clone in new[] { "personal/rig", "personal/blobflags", "rtkelly-labs/Iceberg.NET", "rtkelly-labs/Diffra" })
        {
            _ = Directory.CreateDirectory(Path.Join(_code, clone));
        }

        _ = Directory.CreateDirectory(Path.Join(_root, "rig", "config"));
        File.WriteAllText(ScopesFile, """{"personal": {"owners": ["rtkelly13"], "orgs": ["rtkelly-labs"]}}""");
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string ScopesFile => Path.Join(_root, "rig", "config", EstateScopes.FileName);

    private string ConfigFile => Path.Join(_root, "rig", "config", "managed-projects.json");

    private string Home => Path.Join(_root, "home");

    private string Estate => Path.Join(_root, "estate");

    private string Work => Path.Join(_root, "work");

    private string[] Profiled => ["--orca-data", Estate, "--work-orca-data", Work];

    // ---------------------------------------------------------------- estate orca

    [Fact]
    public async Task Orca_applies_then_reports_what_is_left()
    {
        WriteConfig(extra: new JsonObject { ["name"] = "blobflags-docs", ["repo"] = "git@github.com:rtkelly13/blobflags-docs.git" });
        var estate = Running(Estate, Fixture());
        estate.Repos.Add(Repo("personal/ops", "rtkelly13", null));
        var work = Running(Work, WorkWithDiffra());

        var (code, output, error) = await RunAsync(["estate", "orca", "--code-root", _code, .. Profiled, "--apply", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal([(OrcaLayout.Unmanaged, "ops")], Kinds(output));
        Assert.Empty(work.Repos);
        Assert.Empty(work.Groups);
        Assert.Contains("registered Diffra", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orca_carries_on_when_the_work_profile_is_not_running()
    {
        WriteConfig();
        var estate = Running(Estate, Fixture());
        _ = await OrcaLayout.ApplyAsync(estate.Runtime(), OrcaLayout.LoadDeclared(ConfigFile), _code, _ => true, _ => Task.FromResult<bool?>(false), _ => { }, cancellationToken: Token);

        var (code, output, error) = await RunAsync(["estate", "orca", "--code-root", _code, .. Profiled, "--json"]);

        Assert.Equal(0, code);
        Assert.Equal("[]\n", output);
        Assert.Contains("work Orca profile not checked", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orca_never_evicts_the_estate_when_both_profiles_are_one_folder()
    {
        WriteConfig();
        var estate = Running(Estate, Fixture());
        var same = Path.Join(Estate, "..", "estate");

        var (code, _, _) = await RunAsync(["estate", "orca", "--code-root", _code, "--orca-data", Estate, "--work-orca-data", same, "--apply", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal([Endpoint(Estate)], _profiles.Dialled.Distinct());
        Assert.Equal(Layout.Length, estate.Repos.Count);
    }

    [Fact]
    public async Task Orca_with_repo_applies_and_reports_only_the_named_project()
    {
        WriteConfig();
        var estate = Running(Estate, Fixture());
        var work = Running(Work, WorkWithDiffra());

        var (code, output, _) = await RunAsync(["estate", "orca", "--code-root", _code, .. Profiled, "--apply", "--repo", "Diffra", "--json"]);

        Assert.Equal(0, code);
        Assert.Equal("[]\n", output);
        var groups = estate.Repos.ToDictionary(r => (string)r["displayName"]!, r => (string?)r["projectGroupId"]);
        Assert.Equal(("g-labs", (string?)null), (groups["Diffra"], groups["Iceberg.NET"]));
        _ = Assert.Single(work.Repos);
        Assert.Empty(work.Calls);
    }

    [Fact]
    public async Task Orca_refuses_a_repo_that_is_not_managed_before_asking_Orca()
    {
        WriteConfig();
        _ = Running(Estate, Fixture());

        var (code, _, error) = await RunAsync(["estate", "orca", "--code-root", _code, .. Profiled, "--repo", "Difra", "--repo", "Abc"]);

        Assert.Equal(2, code);
        Assert.Equal("not a managed project: Abc, Difra\n", error);
        Assert.Empty(_profiles.Dialled);
    }

    [Fact]
    public async Task Orca_exits_3_when_Orca_is_not_running()
    {
        await File.WriteAllTextAsync(ConfigFile, """{"projects": []}""", Token);

        var (code, _, error) = await RunAsync(["estate", "orca", "--orca-data", Path.Join(_root, "no-orca")]);

        Assert.Equal(OrcaCommand.Unavailable, code);
        Assert.Contains("is Orca running?", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"projects": [{"name": 3}]}""")]
    [InlineData("not json")]
    public async Task Orca_exits_2_on_a_malformed_config(string text)
    {
        await File.WriteAllTextAsync(ConfigFile, text, Token);
        _ = Running(Estate, Fixture());

        var (code, _, _) = await RunAsync(["estate", "orca", "--orca-data", Estate]);

        Assert.Equal(2, code);
        Assert.Empty(_profiles.Dialled);
    }

    [Fact]
    public async Task Orca_reads_the_skip_list_from_the_machine_config()
    {
        WriteConfig();
        var estate = Running(Estate, Fixture());
        var machine = Path.Join(Home, ".workspace", "config.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(machine)!);
        await File.WriteAllTextAsync(machine, """{"tools": {"estate": {"skip_repos": ["Diffra"]}}}""", Token);

        var (code, output, _) = await RunAsync(["estate", "orca", "--code-root", _code, "--orca-data", Estate, "--work-orca-data", Estate, "--apply", "--json"], machine);

        Assert.Equal(0, code);
        Assert.Equal("[]\n", output);
        Assert.DoesNotContain(estate.Calls, call => call.Method == "repo.add");
    }

    [Fact]
    public async Task Orca_warns_when_GitHub_cannot_say_which_repos_are_archived_and_prints_a_table()
    {
        WriteConfig();
        _ = Running(Estate, Fixture());

        var (code, output, error) = await RunAsync(["estate", "orca", "--code-root", _code, "--orca-data", Estate], github: new FakeGitHub());

        Assert.Equal(0, code);
        Assert.Contains("GitHub could not be asked which repos are archived", error, StringComparison.Ordinal);
        Assert.StartsWith("3 difference(s) from the declared Orca layout\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orca_says_so_when_Orca_already_matches()
    {
        WriteConfig();
        var estate = Running(Estate, Fixture());
        _ = await OrcaLayout.ApplyAsync(estate.Runtime(), OrcaLayout.LoadDeclared(ConfigFile), _code, _ => true, _ => Task.FromResult<bool?>(false), _ => { }, cancellationToken: Token);

        var (code, output, _) = await RunAsync(["estate", "orca", "--code-root", _code, "--orca-data", Estate]);

        Assert.Equal(0, code);
        Assert.Equal("Orca matches managed-projects.json\n", output);
        var asked = Assert.Single(_github.Calls);
        Assert.Contains("rtkelly-labs", asked.Body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- estate orca-worktrees

    [Theory]
    [InlineData]
    [InlineData("--all", "--repo", "rig")]
    public async Task Worktrees_needs_exactly_one_scope(params string[] scope)
    {
        _ = Running(Estate, Fixture());

        var (code, _, error) = await RunAsync(["estate", "orca-worktrees", "--orca-data", Estate, .. scope]);

        Assert.Equal(2, code);
        Assert.Contains("Choose a scope", error, StringComparison.Ordinal);
        Assert.Empty(_profiles.Dialled);
    }

    [Fact]
    public async Task Worktrees_exits_2_when_the_estate_Orca_is_not_running()
    {
        var (code, _, error) = await RunAsync(["estate", "orca-worktrees", "--all", "--orca-data", Estate]);

        Assert.Equal(2, code);
        Assert.Contains("is Orca running?", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worktrees_refuses_a_repo_that_matches_nothing_or_too_much()
    {
        var estate = Running(Estate, Fixture());
        estate.Repos.Add(FakeOrca.Project("r-other", "/elsewhere/blobflags", "blobflags"));

        var (unmatched, _, nothing) = await RunAsync(["estate", "orca-worktrees", "--repo", "nope", .. Profiled]);
        var (ambiguous, _, two) = await RunAsync(["estate", "orca-worktrees", "--repo", "blobflags", .. Profiled]);

        Assert.Equal((2, 2), (unmatched, ambiguous));
        Assert.Contains("--repo nope matches no Orca project. Pass a name or a path.", nothing, StringComparison.Ordinal);
        Assert.Contains("work Orca profile not checked", nothing, StringComparison.Ordinal);
        Assert.Contains("--repo blobflags is ambiguous: it names more than one Orca project.", two, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worktrees_reports_a_vanished_worktree_as_finished()
    {
        var estate = Running(Estate, Project());
        _ = estate.AddWorktree(Path.Join(_root, "gone"), "feature");

        var (code, output, _) = await RunAsync(["estate", "orca-worktrees", "--all", "--personal-only", "--orca-data", Estate]);

        Assert.Equal(0, code);
        Assert.Equal(
            """
            1 of 1 Orca worktree(s) finished; the rest are kept
            profile   project  worktree  branch   verdict  reason
            personal  app      gone      feature  missing  the folder is gone

            """,
            output);
        Assert.Empty(estate.Removed);
    }

    [Fact]
    public async Task Worktrees_exits_1_when_a_removal_fails()
    {
        var estate = Running(Estate, Project());
        var id = estate.AddWorktree(Path.Join(_root, "gone"), "feature");
        _ = estate.Stuck.Add(id);

        var (code, output, _) = await RunAsync(["estate", "orca-worktrees", "--repo", "app", "--orca-data", Estate, "--apply", "--json"]);

        Assert.Equal(1, code);
        var row = Assert.Single(JsonNode.Parse(output)!.AsArray())!.AsObject();
        Assert.Equal(
            ["profile", "project", "path", "branch", "verdict", "reason", "outcome", "worktree_id"],
            row.Select(p => p.Key));
        Assert.StartsWith("failed", (string)row["outcome"]!, StringComparison.Ordinal);
        Assert.Equal([id], estate.Removed);
    }

    [Fact]
    public void The_applied_headline_counts_each_outcome()
    {
        WorktreeRow Row(string verdict, string outcome) => new("personal", "app", "/a", "b", verdict, "r", outcome);

        var headline = OrcaWorktreesCommand.Headline(
            [Row(OrcaWorktrees.Missing, "removed"), Row(OrcaWorktrees.Merged, "failed: no"), Row(OrcaWorktrees.Landed, "skipped: back"), Row(OrcaWorktrees.Current, string.Empty)],
            applied: true);

        Assert.Equal("1 of 4 Orca worktree(s) removed, 1 failed, 1 skipped as changed since review; the rest are kept", headline);
    }

    // ---------------------------------------------------------------- helpers

    private static List<(string, string)> Kinds(string json) =>
        [.. JsonNode.Parse(json)!.AsArray().Select(f => ((string)f!["kind"]!, (string)f["repo"]!))];

    private static string Endpoint(string folder) => Path.Join(folder, "orca.sock");

    private string Posix(string clone) => Path.Join(_code, clone).Replace('\\', '/');

    private JsonObject Repo(string clone, string owner, string? groupId) => FakeOrca.Project($"r-{Path.GetFileName(clone)}", Posix(clone), Path.GetFileName(clone), groupId, owner);

    private void WriteConfig(JsonObject? extra = null)
    {
        var projects = new JsonArray([.. Layout.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name,
            ["repo"] = $"git@github.com:{d.Owner}/{d.Name}.git",
            ["orca"] = new JsonObject { ["group"] = d.Group },
        })]);
        if (extra is not null)
        {
            projects.Add(extra);
        }

        File.WriteAllText(ConfigFile, new JsonObject { ["projects"] = projects }.ToJsonString());
    }

    // The estate as the Python fixture held it: rig and blobflags in personal/tooling, Iceberg.NET at the top level.
    private FakeOrca Fixture() => new FakeOrca
    {
        Repos = { Repo("personal/rig", "rtkelly13", "g-tooling"), Repo("personal/blobflags", "rtkelly13", "g-tooling"), Repo("rtkelly-labs/Iceberg.NET", "rtkelly-labs", null) },
        Groups =
        {
            FakeOrca.Group("g-personal", "personal", parentPath: Posix("personal")),
            FakeOrca.Group("g-tooling", "tooling", "g-personal"),
            FakeOrca.Group("g-labs", "rtkelly-labs", parentPath: Posix("rtkelly-labs")),
        },
    };

    private FakeOrca WorkWithDiffra() => new FakeOrca
    {
        Repos = { Repo("rtkelly-labs/Diffra", "rtkelly-labs", "g-labs") },
        Groups = { FakeOrca.Group("g-labs", "rtkelly-labs") },
    };

    // One project, r1, whose worktrees FakeOrca.AddWorktree lists.
    private FakeOrca Project() => new FakeOrca { Repos = { FakeOrca.Project("r1", Path.Join(_root, "app").Replace('\\', '/'), "app") } };

    // Starts a fake as the Orca of the profile in folder: its runtime file names its own endpoint.
    private FakeOrca Running(string folder, FakeOrca template)
    {
        var fake = new FakeOrca { Listening = new(OrcaTransport.Unix, Endpoint(folder)) };
        fake.Repos.AddRange(template.Repos.Select(r => r.DeepClone().AsObject()));
        fake.Groups.AddRange(template.Groups.Select(g => g.DeepClone().AsObject()));
        _ = Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Join(folder, OrcaLocations.RuntimeFile), fake.Metadata);
        _profiles.ByEndpoint[fake.Listening.Endpoint] = fake;
        return fake;
    }

    private async Task<(int Code, string Out, string Err)> RunAsync(string[] args, string? machineConfig = null, FakeGitHub? github = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var locations = new OrcaLocations(_ => null, Home, OrcaOs.Linux);
        var command = new EstateCommand(
            new EstateHost(_root, Home, _root, ScopesFile, machineConfig ?? Path.Join(Home, ".workspace", "config.json"), ManagedProjectsFile: ConfigFile),
            new OrcaHost(locations, _profiles, error),
            _proc,
            github ?? _github,
            TimeProvider.System,
            _ => null,
            output,
            error);
        var code = await new RootCommand { command.Build() }.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"), error.ToString().ReplaceLineEndings("\n"));
    }

    // Each profile's Orca by the endpoint its runtime file names.
    private sealed class Profiles : IOrcaExchange
    {
        public Dictionary<string, FakeOrca> ByEndpoint { get; } = new(StringComparer.Ordinal);

        public List<string> Dialled { get; } = [];

        public async IAsyncEnumerable<string> ExchangeAsync(OrcaTransport transport, string request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Dialled.Add(transport.Endpoint);
            await foreach (var line in ByEndpoint[transport.Endpoint].ExchangeAsync(transport, request, cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }
    }
}
