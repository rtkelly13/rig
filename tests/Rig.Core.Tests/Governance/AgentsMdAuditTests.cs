using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Governance;

/// <summary>The AGENTS.md audit: each deterministic check, the waivers that dismiss them, and the docs-tree walk.</summary>
public sealed class AgentsMdAuditTests : IDisposable
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

    private static readonly AgentsMdRules Any = new() { ClaudeMd = "any" };
    private static readonly AgentsMdRules Symlink = new() { ClaudeMd = "symlink" };

    private readonly TempDir _dir = new();
    private readonly FakeProc _proc = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Repo => _dir.Path;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_clean_repo_has_no_findings()
    {
        var result = await Audit(GoodRoot);

        Assert.Empty(result.Findings);
        Assert.False(result.Failed);
        Assert.Equal(Path.GetFileName(Repo), result.Repo);
    }

    [Fact]
    public async Task A_missing_agents_md_is_an_error()
    {
        var result = await Subject().AuditAsync(Repo, AgentsMdRules.Default, Token);

        Assert.True(result.Failed);
        Assert.Equal(["required-content"], Checks(result));
        Assert.Empty(result.Metrics);
    }

    [Fact]
    public async Task The_root_file_has_a_line_budget()
    {
        var small = await Audit("a\nb\nc", new() { ClaudeMd = "any", MaxLines = 10, Require = [], CheckPaths = false });
        var big = await Audit(string.Join('\n', Enumerable.Repeat("x", 200)), new() { ClaudeMd = "any", MaxLines = 10, Require = [] });

        Assert.Empty(small.Findings);
        var finding = Assert.Single(big.Findings);
        Assert.Equal(("root-size", AgentsMdSeverity.Error), (finding.Check, finding.Severity));
        Assert.Equal("200 lines against a budget of 10. Move domain rules into `docs/` and point at them.", finding.Message);
    }

    [Fact]
    public async Task A_duplicate_heading_is_reported_at_its_second_occurrence()
    {
        var result = await Audit(GoodRoot + "## Conventions\n\nfoo\n\n## Other\n\n## Conventions\n");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("duplicate-heading", finding.Check);
        Assert.Equal(18, finding.Line);
        Assert.Contains("line 12", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Path_liveness_needs_a_separator_and_skips_home_paths()
    {
        _ = _dir.File("src/real.ts");

        var result = await Audit(GoodRoot + "See `src/real.ts` and `src/gone.ts`, avoid `.test.ts` and `Prose.tsx`.\nSet it in `~/.workspace/config.json`.\n");

        Assert.Equal(["src/gone.ts"], result.Findings.Where(f => f.Check == "path-liveness").Select(f => f.Message.Split('`')[1]));
    }

    [Fact]
    public async Task A_dead_link_breaks_pointer_integrity()
    {
        var result = await Audit(GoodRoot + "\nSee [docs](./docs/missing.md).\n");

        Assert.Equal(["pointer-integrity"], Checks(result));
        Assert.True(result.Failed);
    }

    [Fact]
    public async Task A_nul_link_is_a_dead_pointer_not_a_crash()
    {
        Write("docs/README.md", "See [bad](./bad%00.md).\n");

        var result = await Audit(GoodRoot + "\nSee [docs](./docs/README.md).\n");

        Assert.Equal(["docs/README.md"], Paths(result, "pointer-integrity"));
    }

    [Fact]
    public async Task A_percent_encoded_link_resolves()
    {
        Write("docs/Repo Docs Index.md");

        var result = await Audit(GoodRoot + "\nSee [index](./docs/Repo%20Docs%20Index.md).\n");

        Assert.Empty(Paths(result, "pointer-integrity"));
    }

    [Fact]
    public async Task An_orphan_doc_is_a_warning_and_a_linked_one_is_not()
    {
        Write("docs/linked.md");
        Write("docs/orphan.md");

        var result = await Audit(GoodRoot + "\nSee [linked](./docs/linked.md).\n");

        Assert.Equal(["docs/orphan.md"], Paths(result, "docs-orphan"));
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("index.md")]
    public async Task An_index_file_makes_its_folder_self_indexing(string index)
    {
        Write($"docs/adr/{index}");
        Write("docs/adr/0001-thing.md");

        var result = await Audit(GoodRoot);

        Assert.Empty(Paths(result, "docs-orphan"));
    }

    [Fact]
    public async Task A_link_inside_code_is_an_example_not_a_pointer()
    {
        var result = await Audit(
            GoodRoot + "\nUse `![alt](./shot.png)` in the body.\n\n```\n[x](./also-fake.md)\n```\n",
            new() { ClaudeMd = "any", CheckPaths = false });

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Strip_code_leaves_real_links_alone()
    {
        var stripped = AgentsMdAudit.StripCode("See [real](./docs/x.md) and `[fake](./y.md)`.\n");

        Assert.Contains("./docs/x.md", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("./y.md", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_generation_marker_fails()
    {
        var result = await Audit("<!-- generated by init -->\n" + GoodRoot);

        Assert.Contains("generation-marker", Checks(result));
        Assert.True(result.Failed);
    }

    [Fact]
    public async Task Required_content_reports_each_missing_piece()
    {
        var result = await Audit("# bare\n\n## Heading\n", new() { ClaudeMd = "any", CheckPaths = false });

        Assert.Equal(3, result.Findings.Where(f => f.Check == "required-content").Select(f => f.Message).Distinct().Count());
    }

    [Fact]
    public async Task A_small_docs_tree_needs_no_index_and_a_large_one_does()
    {
        Write("docs/a.md");
        var small = await Audit(GoodRoot + "\nSee [a](./docs/a.md).\n");
        Write("docs/b.md");
        Write("docs/c.md");
        var large = await Audit(GoodRoot, new() { ClaudeMd = "any", DocsIndexMinFiles = 3 });

        Assert.Empty(Paths(small, "docs-index"));
        Assert.Equal(["docs/README.md"], Paths(large, "docs-index"));
        Assert.All(large.Findings.Where(f => f.Check == "docs-index"), f => Assert.Equal(AgentsMdSeverity.Warn, f.Severity));
    }

    [Fact]
    public async Task The_docs_index_names_each_top_level_entry_it_misses()
    {
        Write("docs/README.md", "# docs\n\n- [a](a.md)\n- [git](git/README.md)\n");
        Write("docs/a.md");
        Write("docs/missed.md");
        Write("docs/git/README.md", "# git\n\n- [stacking](stacking.md)\n");
        Write("docs/git/stacking.md");
        Write("docs/estate/README.md", "# estate\n");

        var result = await Audit(GoodRoot + "\nSee [docs](./docs/README.md).\n", new() { ClaudeMd = "any", DocsIndexMinFiles = 3 });

        Assert.Equal(["docs/estate", "docs/missed.md"], Paths(result, "docs-index").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_folder_needs_a_readme_that_lists_its_siblings()
    {
        Write("docs/plans/one.md");
        Write("docs/git/README.md", "# git\n\n- [stacking](stacking.md)\n");
        Write("docs/git/stacking.md");
        Write("docs/git/worktrees.md");

        var result = await Audit(GoodRoot);

        Assert.Equal(["docs/git/worktrees.md", "docs/plans/README.md"], Paths(result, "folder-index").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_docs_index_link_through_a_folder_without_a_readme_counts_for_it()
    {
        Write("docs/README.md", "# docs\n\n- [a](a.md)\n- [plans](superpowers/plans/README.md)\n");
        Write("docs/a.md");
        Write("docs/b.md");
        Write("docs/superpowers/plans/README.md", "# plans\n\n- [one](one.md)\n");
        Write("docs/superpowers/plans/one.md");
        Write("docs/git/README.md", "# git\n\n- [stacking](stacking.md)\n");
        Write("docs/git/stacking.md");

        var result = await Audit(GoodRoot + "\nSee [docs](./docs/README.md).\n", new() { ClaudeMd = "any", DocsIndexMinFiles = 3 });

        Assert.Equal(["docs/b.md", "docs/git"], Paths(result, "docs-index").Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_docs_site_hub_serves_its_subfolders()
    {
        Write("docs/index.md", "# hub\n\n- [a](guides/a.md)\n- [b](guides/b.md)\n- [c](reference/c.md)\n");
        foreach (var page in new[] { "guides/a.md", "guides/b.md", "reference/c.md", "reference/d.md" })
        {
            Write($"docs/{page}");
        }

        var result = await Audit(GoodRoot + "\nSee [docs](./docs/index.md).\n", new() { ClaudeMd = "any", DocsIndexMinFiles = 3 });

        Assert.Empty(Paths(result, "docs-index"));
        Assert.Equal(["docs/reference/README.md"], Paths(result, "folder-index"));
    }

    [Fact]
    public async Task Every_finding_check_is_classified()
    {
        foreach (var name in "abcdef")
        {
            Write($"docs/{name}.md");
        }

        Write("docs/plans/one.md");

        var checks = Checks(await Audit(GoodRoot)).ToHashSet();

        Assert.Contains("docs-index", checks);
        Assert.Contains("folder-index", checks);
        Assert.Subset(AgentsMdAudit.GatingChecks.Concat(AgentsMdAudit.ReportOnlyChecks).ToHashSet(), checks);
    }

    [Fact]
    public async Task A_long_code_block_in_docs_is_reported_but_a_diagram_is_not()
    {
        var body = string.Join('\n', Enumerable.Range(0, 5).Select(n => $"line {n}"));
        var longBody = string.Join('\n', Enumerable.Range(0, 25).Select(n => $"line {n}"));
        Write("docs/short.md", $"# s\n\n```bash\n{body}\n```\n");
        Write("docs/long.md", $"# l\n\n```json\n{longBody}\n```\n");
        Write("docs/diagram.md", $"# d\n\n```mermaid\n{longBody}\n```\n");

        var result = await Audit(GoodRoot);

        var block = Assert.Single(result.Findings, f => f.Check == "docs-code-block");
        Assert.Equal("docs/long.md", block.Path);
        Assert.Contains("25-line code block at line 3", block.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_linked_folder_is_listed_but_not_walked()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege Windows CI does not grant.");
        using var outside = new TempDir();
        _ = outside.File("deep/stray.md");
        _ = Directory.CreateSymbolicLink(Path.Join(_dir.Folder("docs"), "linked"), outside.Path);

        var result = await Audit(GoodRoot);

        Assert.Empty(Paths(result, "docs-orphan"));
    }

    [Fact]
    public async Task A_claude_md_copy_is_flagged_but_a_link_is_not()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege Windows CI does not grant.");
        var copy = Path.Join(Repo, "CLAUDE.md");
        await File.WriteAllTextAsync(copy, "# copy\n", Token);

        var copied = await Audit(GoodRoot, Symlink);
        File.Delete(copy);
        _ = File.CreateSymbolicLink(copy, "AGENTS.md");
        var linked = await Audit(GoodRoot, Symlink);

        Assert.Equal(["claude-md-shape"], Checks(copied));
        Assert.Empty(linked.Findings);
    }

    [Fact]
    public async Task Claude_md_absent_policy_warns_when_the_file_exists()
    {
        await File.WriteAllTextAsync(Path.Join(Repo, "CLAUDE.md"), "x", Token);

        var result = await Audit(GoodRoot, new() { ClaudeMd = "absent" });

        Assert.Equal((AgentsMdSeverity.Warn, "CLAUDE.md"), (Assert.Single(result.Findings).Severity, result.Findings[0].Path));
    }

    [Fact]
    public async Task A_committed_link_checked_out_as_a_placeholder_file_is_not_flagged()
    {
        var git = await RealGit();
        await File.WriteAllTextAsync(Path.Join(Repo, "CLAUDE.md"), "AGENTS.md", Token);
        await StageLink(git);

        Assert.Empty((await Audit(GoodRoot, Symlink, git)).Findings);
    }

    [Fact]
    public async Task A_placeholder_edited_into_a_real_file_is_flagged()
    {
        var git = await RealGit();
        await StageLink(git);
        await File.WriteAllTextAsync(Path.Join(Repo, "CLAUDE.md"), "# Claude\n\nInstructions that drift from AGENTS.md\n", Token);

        Assert.Equal(["claude-md-shape"], Checks(await Audit(GoodRoot, Symlink, git)));
    }

    [Fact]
    public async Task A_committed_regular_file_naming_agents_md_is_flagged()
    {
        var git = await RealGit();
        await File.WriteAllTextAsync(Path.Join(Repo, "CLAUDE.md"), "AGENTS.md", Token);
        Assert.True((await git.RunAsync(Repo, ["add", "CLAUDE.md"], cancellationToken: Token)).Ok);

        Assert.Equal(["claude-md-shape"], Checks(await Audit(GoodRoot, Symlink, git)));
    }

    [Fact]
    public async Task A_bespoke_agent_file_is_an_error_unless_disabled()
    {
        Write(".github/copilot-instructions.md", "rules\n");

        var flagged = await Audit(GoodRoot);
        var disabled = await Audit(GoodRoot, new() { ClaudeMd = "any", BespokeAgentFiles = [] });

        var finding = Assert.Single(flagged.Findings);
        Assert.Equal(("bespoke-agent-file", ".github/copilot-instructions.md", AgentsMdSeverity.Error), (finding.Check, finding.Path, finding.Severity));
        Assert.True(flagged.Failed);
        Assert.Empty(disabled.Findings);
    }

    [Fact]
    public async Task A_broken_link_is_still_a_bespoke_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege Windows CI does not grant.");
        _ = File.CreateSymbolicLink(Path.Join(Repo, "GEMINI.md"), "nowhere.md");

        Assert.Equal(["bespoke-agent-file"], Checks(await Audit(GoodRoot)));
    }

    [Fact]
    public async Task A_tool_feature_definition_is_not_flagged()
    {
        Write(".github/agents/workflows.agent.md", "---\ndescription: x\n---\n");

        Assert.Empty((await Audit(GoodRoot)).Findings);
    }

    [Fact]
    public async Task A_reasoned_waiver_suppresses()
    {
        Waivers(new AgentsMdWaiver("pointer-integrity", "AGENTS.md", "doc lands in #123"));

        var result = await Audit(GoodRoot + "\nSee [gone](./docs/missing.md).\n");

        Assert.Empty(result.Findings);
        Assert.Equal(["pointer-integrity"], result.Suppressed.Select(f => f.Check));
    }

    [Theory]
    [InlineData("", null, "waiver-unreasoned")]
    [InlineData(" \u001f\t", null, "waiver-unreasoned")]
    [InlineData("temporary", "2020-01-01", "waiver-expired")]
    [InlineData("temporary", "not-a-date", "waiver-expired")]
    public async Task A_waiver_without_a_reason_or_past_its_date_does_not_suppress(string reason, string? expires, string check)
    {
        Waivers(new AgentsMdWaiver("pointer-integrity", "AGENTS.md", reason, expires));

        var result = await Audit(GoodRoot + "\nSee [gone](./docs/missing.md).\n");

        Assert.Contains(check, Checks(result));
        Assert.Contains("pointer-integrity", Checks(result));
        Assert.True(result.Failed);
    }

    [Fact]
    public async Task A_stale_waiver_is_reported_as_info()
    {
        Waivers(new AgentsMdWaiver("root-size", "AGENTS.md", "was long, now fixed"));

        var result = await Audit(GoodRoot);

        var stale = Assert.Single(result.Findings, f => f.Check == "waiver-stale");
        Assert.Equal(AgentsMdSeverity.Info, stale.Severity);
        Assert.False(result.Failed);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"waivers": [{"check": "root-size", "path": 3, "reason": "r"}]}""")]
    [InlineData("""{"waivers": {}}""")]
    [InlineData("[]")]
    public async Task A_malformed_waiver_file_is_ignored_whole(string text)
    {
        await File.WriteAllTextAsync(Path.Join(Repo, AgentsMdAudit.WaiverFileName), text, Token);

        Assert.Empty(AgentsMdAudit.LoadWaivers(Repo));
        Assert.Empty((await Audit(GoodRoot)).Findings);
    }

    [Fact]
    public void Waivers_round_trip_through_the_file()
    {
        AgentsMdWaiver[] waivers = [new("a", "b", "c", "2026-01-02"), new("d", "e")];
        File.WriteAllText(Path.Join(Repo, AgentsMdAudit.WaiverFileName), AgentsMdAudit.RenderWaivers(waivers));

        Assert.Equal(waivers, AgentsMdAudit.LoadWaivers(Repo));
        Assert.Equal(
            "{\n  \"waivers\": [\n    {\n      \"check\": \"d\",\n      \"path\": \"e\",\n      \"reason\": \"\",\n      \"expires\": null\n    }\n  ]\n}\n",
            AgentsMdAudit.RenderWaivers([waivers[1]]));
    }

    [Fact]
    public void A_waiver_expires_the_day_after_its_date()
    {
        var waiver = new AgentsMdWaiver("x", "y", "z", "2026-06-15");

        Assert.False(waiver.ExpiredOn(new DateOnly(2026, 6, 15)));
        Assert.True(waiver.ExpiredOn(new DateOnly(2026, 6, 16)));
        Assert.False(new AgentsMdWaiver("x", "y", "z").ExpiredOn(DateOnly.MaxValue));
    }

    [Theory]
    [InlineData("2026-06-15", "2026-06-15")]
    [InlineData("20260615", "2026-06-15")]
    [InlineData("2026-02-30", null)]
    [InlineData("2026-06-15\n", null)]
    [InlineData("2026-6-15", null)]
    [InlineData("0000-01-01", null)]
    public void Iso_dates_parse_as_python_reads_them(string text, string? expected) =>
        Assert.Equal(expected, AgentsMdAudit.ParseIsoDate(text)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task Metrics_are_collected_but_never_gated()
    {
        var metrics = AgentsMdAudit.CollectMetrics("## A\n\n1. do this\n2. do that\n\nYou MUST and you NEVER.\n");
        var result = await Audit(GoodRoot);

        Assert.Equal([new("instruction-count", 2), new("forcing-language", 2), new KeyValuePair<string, int>("section-outlier", 5)], metrics);
        Assert.Equal(["instruction-count", "forcing-language", "section-outlier"], result.Metrics.Select(m => m.Key));
        Assert.False(result.Failed);
    }

    [Fact]
    public void Split_lines_breaks_where_python_does()
    {
        Assert.Equal(["a", "b", "c", "d", "", "e"], AgentsMdAudit.SplitLines("a\r\nb\rc\u2028d\n\u000ce"));
        Assert.Empty(AgentsMdAudit.SplitLines(string.Empty));
        Assert.Equal(["x"], AgentsMdAudit.SplitLines("x\n"));
    }

    [Fact]
    public async Task The_json_report_carries_every_field()
    {
        var result = await Audit(GoodRoot + "\nSee [gone](./docs/missing.md).\n");

        Assert.Equal(
            """{"repo": "R", "findings": [{"check": "pointer-integrity", "severity": "error", "path": "AGENTS.md", "message": "M", "line": null}], "suppressed": [], "metrics": {"instruction-count": 0, "forcing-language": 0, "section-outlier": 6}}""",
            Normalised(result));
    }

    private static List<string> Checks(AgentsMdAuditResult result) => [.. result.Findings.Select(f => f.Check)];

    private static List<string> Paths(AgentsMdAuditResult result, string check) => [.. result.Findings.Where(f => f.Check == check).Select(f => f.Path)];

    // The JSON with the repo name and messages blanked, compact, so the shape is what is compared.
    private static string Normalised(AgentsMdAuditResult result)
    {
        var json = result.ToJson();
        json["repo"] = "R";
        foreach (var finding in json["findings"]!.AsArray())
        {
            finding!["message"] = "M";
        }

        return PyJson.DumpsLine(json);
    }

    private AgentsMdAudit Subject(Git? git = null) =>
        new(git ?? new Git(_proc, Repo), new FixedClock(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero)));

    private async Task<AgentsMdAuditResult> Audit(string agents, AgentsMdRules? rules = null, Git? git = null)
    {
        await File.WriteAllTextAsync(Path.Join(Repo, "AGENTS.md"), agents, Token);
        return await Subject(git).AuditAsync(Repo, rules ?? Any, Token);
    }

    private void Write(string relative, string text = "# doc\n") => File.WriteAllText(_dir.File(relative), text);

    private void Waivers(params AgentsMdWaiver[] waivers) =>
        File.WriteAllText(Path.Join(Repo, AgentsMdAudit.WaiverFileName), AgentsMdAudit.RenderWaivers(waivers));

    private async Task<Git> RealGit()
    {
        var git = new Git(new SystemProc(), Repo);
        Assert.True((await git.RunAsync(Repo, ["init", "-q"], cancellationToken: Token)).Ok);
        return git;
    }

    // Stages CLAUDE.md as a link to AGENTS.md without making one, as a checkout without link support leaves it.
    private async Task StageLink(Git git)
    {
        var blob = (await git.RunAsync(Repo, ["hash-object", "-w", "--stdin"], stdinText: "AGENTS.md", cancellationToken: Token)).OutOrEmpty;
        Assert.True((await git.RunAsync(Repo, ["update-index", "--add", "--cacheinfo", $"120000,{blob},CLAUDE.md"], cancellationToken: Token)).Ok);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
