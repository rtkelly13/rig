using System.CommandLine;
using System.Text.Json.Nodes;
using Rig.Cli.Governance;
using Rig.Core.Governance;
using Rig.Core.Processes;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;

namespace Rig.Cli.Tests.Governance;

/// <summary>
/// <c>rig repo-governance agents-audit</c> end to end over checkouts in a temp folder. Git and GitHub are faked, so no real
/// repo or API is touched.
/// </summary>
public sealed class GovernanceCommandTests : IDisposable
{
    private const string GoodRoot = """
        # thing

        A React component library for accessible data visualization.

        This project uses pnpm.

        ## Commands

        | | |
        |---|---|
        | `pnpm build` | builds it |

        """;

    private const string Bespoke = "Merge anything worth keeping into AGENTS.md, then delete this. The tool that owns this name reads it instead of AGENTS.md.";

    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Checkout => Path.Join(_root.Path, "checkout");

    [Fact]
    public async Task A_clean_repo_gets_one_line_and_passes()
    {
        var repo = await RepoAsync("good");

        var (code, output) = await RunAsync(repo);

        Assert.Equal((0, "✓ good: clean\n"), (code, output));
    }

    [Fact]
    public async Task Findings_render_as_a_table_with_metrics_and_fail()
    {
        var repo = await RepoAsync("bad", "GEMINI.md");

        var (code, output) = await RunAsync(Path.GetDirectoryName(repo)!, "bad");

        Assert.Equal(1, code);
        var lines = output.Split('\n');
        Assert.Equal("bad — AGENTS.md audit", lines[0]);
        Assert.Matches(@"^Check\s+Where\s+Finding$", lines[1]);
        Assert.Matches($@"^bespoke-agent-file\s+GEMINI\.md\s+{System.Text.RegularExpressions.Regex.Escape(Bespoke)}$", lines[2]);
        Assert.Equal("  metrics (not gated): instruction-count 0, forcing-language 0, section-outlier 6", lines[3]);
        Assert.Equal(["", ""], lines[4..]);
    }

    [Fact]
    public async Task The_json_payload_keeps_python_s_keys_and_order()
    {
        var repo = await RepoAsync("bad", "GEMINI.md");

        var (code, output) = await RunAsync(repo, "--json");

        Assert.Equal(1, code);
        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(output));
        Assert.Equal(["results", "remote", "remote_unread", "gating_checks", "report_only_checks", "rubric"], payload.Select(p => p.Key));
        var result = payload["results"]![0]!;
        Assert.Equal(["repo", "findings", "suppressed", "metrics"], result.AsObject().Select(p => p.Key));
        Assert.Equal(["check", "severity", "path", "message", "line"], result["findings"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal(AgentsMdAudit.GatingChecks, payload["gating_checks"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("docs/agents/agent-consistency.md", (string)payload["rubric"]!);
        Assert.StartsWith("{\n  \"results\": [\n    {\n      \"repo\": \"bad\",", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warnings_fail_only_under_strict()
    {
        var repo = await RepoAsync("warned");
        await File.WriteAllTextAsync(
            Path.Join(repo, AgentsMdAudit.WaiverFileName),
            """{"waivers": [{"check": "root-size", "path": "AGENTS.md", "reason": "big", "expires": "2020-01-01"}]}""",
            Token);

        var (lax, _) = await RunAsync(repo);
        var (strict, output) = await RunAsync(repo, "--strict");

        Assert.Equal((0, 1), (lax, strict));
        Assert.Contains("waiver-expired", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_policy_in_the_working_directory_wins_over_the_checkout_s()
    {
        var repo = await RepoAsync("local", "GEMINI.md");
        await WriteAsync(Path.Join(repo, "config", RepoPolicies.FileName), """{"defaults": {"agents_md_hygiene": {"bespoke_agent_files": []}}}""");
        await WriteAsync(Path.Join(Checkout, "config", RepoPolicies.FileName), "{}");

        Assert.Equal(Path.Join(repo, "config", RepoPolicies.FileName), Host(repo).PolicyFile);
        Assert.Equal(Path.Join(Checkout, "config", RepoPolicies.FileName), Host(_root.Path).PolicyFile);
        Assert.Equal(0, (await RunAsync(repo)).Code);
    }

    [Fact]
    public async Task A_missing_path_audits_nothing_and_passes()
    {
        var (code, output) = await RunAsync(_root.Path, "absent");

        Assert.Equal((0, string.Empty), (code, output));
    }

    [Fact]
    public async Task Remote_alone_sweeps_github_without_touching_a_checkout()
    {
        _ = _proc.Answer(Listing(("rtkelly13/docs", false), ("rtkelly13/old", true)), "gh", "repo", "list", "rtkelly13");
        _ = _proc.Answer(Listing(), "gh", "repo", "list", "rtkelly-labs");
        _ = _proc.Answer(TreeOf("CLAUDE.md"), "gh", "api");

        var (code, output) = await RunAsync(_root.Path, "--remote");

        Assert.Equal(1, code);
        Assert.DoesNotContain(_proc.Requests, r => r.Argv[0] == "git");
        var lines = output.Split('\n');
        Assert.Equal("GitHub default branches — redundant agent files", lines[0]);
        Assert.Matches(@"^rtkelly13/docs\s+CLAUDE\.md\s+remove$", lines[2]);
        Assert.Matches(@"^rtkelly13/old\s+CLAUDE\.md\s+archived — left as-is$", lines[3]);
    }

    [Fact]
    public async Task Archived_findings_alone_pass_and_an_unread_owner_fails()
    {
        _ = _proc.Answer(Listing(("rtkelly13/old", true)), "gh", "repo", "list", "rtkelly13");
        _ = _proc.Answer(Listing(), "gh", "repo", "list", "rtkelly-labs");
        _ = _proc.Answer(TreeOf("CLAUDE.md"), "gh", "api");

        Assert.Equal(0, (await RunAsync(_root.Path, "--remote", "--json")).Code);

        _ = _proc.Answer(new Ran(1, string.Empty, "auth"), "gh", "repo", "list", "rtkelly-labs");
        var (code, output) = await RunAsync(_root.Path, "--remote");

        Assert.Equal(1, code);
        Assert.StartsWith("✗ GitHub sweep could not read rtkelly-labs: check `gh auth status` and retry\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_clean_sweep_says_so()
    {
        _ = _proc.Answer(Listing(), "gh", "repo", "list");
        var repo = await RepoAsync("good");

        var (code, output) = await RunAsync(repo, "--remote", ".");

        Assert.Equal((0, "✓ good: clean\n✓ GitHub sweep: no redundant agent files\n"), (code, output));
    }

    [Fact]
    public async Task Write_waivers_stubs_each_new_gating_finding_once()
    {
        var repo = await RepoAsync("stub", "GEMINI.md", ".cursorrules");
        await WriteAsync(Path.Join(repo, AgentsMdAudit.WaiverFileName), AgentsMdAudit.RenderWaivers([new("bespoke-agent-file", "GEMINI.md", "kept for a tool")]));

        var (code, output) = await RunAsync(repo, "--json", "--write-waivers");
        var (_, again) = await RunAsync(repo, "--write-waivers");

        Assert.Equal(1, code);
        Assert.EndsWith("stub: wrote 1 stub(s) to .agents-audit.json — fill in each `reason` or the audit stays red.\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("wrote", again, StringComparison.Ordinal);
        Assert.Equal(
            [new AgentsMdWaiver("bespoke-agent-file", "GEMINI.md", "kept for a tool"), new AgentsMdWaiver("bespoke-agent-file", ".cursorrules")],
            AgentsMdAudit.LoadWaivers(repo));
    }

    [Fact]
    public async Task Write_waivers_skips_report_only_and_warning_checks()
    {
        var repo = await RepoAsync("warned");
        await WriteAsync(
            Path.Join(repo, AgentsMdAudit.WaiverFileName),
            """{"waivers": [{"check": "root-size", "path": "AGENTS.md", "reason": "big", "expires": "2020-01-01"}]}""");

        var (_, output) = await RunAsync(repo, "--write-waivers");

        Assert.DoesNotContain("wrote", output, StringComparison.Ordinal);
        Assert.Single(AgentsMdAudit.LoadWaivers(repo));
    }

    private static Ran Listing(params (string Name, bool Archived)[] rows) => new(
        0,
        new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject
        {
            ["nameWithOwner"] = r.Name,
            ["isArchived"] = r.Archived,
            ["isFork"] = false,
            ["defaultBranchRef"] = new JsonObject { ["name"] = "main" },
        })]).ToJsonString(),
        string.Empty);

    private static Ran TreeOf(params string[] paths) =>
        new(0, new JsonObject { ["tree"] = new JsonArray([.. paths.Select(p => (JsonNode)new JsonObject { ["path"] = p })]) }.ToJsonString(), string.Empty);

    private static async Task WriteAsync(string path, string text)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, Token);
    }

    private async Task<string> RepoAsync(string name, params string[] extra)
    {
        var repo = _root.Folder(Path.Join("personal", name));
        await WriteAsync(Path.Join(repo, "AGENTS.md"), GoodRoot);
        foreach (var file in extra)
        {
            await WriteAsync(Path.Join(repo, file), string.Empty);
        }

        return repo;
    }

    private GovernanceHost Host(string cwd) => new(_root.Path, cwd, Checkout);

    private async Task<(int Code, string Out)> RunAsync(string cwd, params string[] args)
    {
        using var output = new StringWriter();
        var command = new GovernanceCommand(Host(cwd), _proc, TimeProvider.System, output);
        var code = await new RootCommand { command.Build() }.Parse(["repo-governance", "agents-audit", .. args])
            .InvokeAsync(new InvocationConfiguration { Output = output, Error = TextWriter.Null }, Token);
        return (code, output.ToString().ReplaceLineEndings("\n"));
    }
}
