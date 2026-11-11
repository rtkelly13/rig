using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Cli.Vault;
using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Cli.Governance;

/// <summary>Where <c>rig repo-governance</c> looks.</summary>
/// <param name="CodeRoot">The code root: its <c>personal</c> folder holds the estate, and git is hardened beneath it.</param>
/// <param name="Cwd">The working directory: the default target, and where a local <c>config/repo_policies.json</c> wins.</param>
/// <param name="Checkout">The rig checkout, whose <c>config/</c> holds the policy and the estate scopes.</param>
internal sealed record GovernanceHost(string CodeRoot, string Cwd, string Checkout)
{
    /// <summary>The personal estate folder.</summary>
    public string PersonalDir => Path.Join(CodeRoot, "personal");

    /// <summary>The estate scopes file, naming the org folders beside the personal folder.</summary>
    public string ScopesFile => Path.Join(Checkout, "config", EstateScopes.FileName);

    /// <summary>The policy: the working directory's <c>config/repo_policies.json</c> when it has one, else the checkout's.</summary>
    public string PolicyFile =>
        Path.Join(Cwd, "config", RepoPolicies.FileName) is var local && File.Exists(local) ? local : Path.Join(Checkout, "config", RepoPolicies.FileName);
}

/// <summary>
/// <c>rig repo-governance</c>: the subcommands ported from <c>src/rig/repo_governance</c>. The group's other subcommands
/// are still Python's, so <see cref="Forwarding.Forwarded"/> hands any line naming one of them over.
/// </summary>
/// <param name="host">Where it looks.</param>
/// <param name="proc">Runs git and gh.</param>
/// <param name="time">The clock waivers expire by.</param>
/// <param name="output">Where reports go.</param>
/// <param name="gitHub">The GitHub REST seam; <c>gh api</c> through <paramref name="proc"/> when null.</param>
internal sealed partial class GovernanceCommand(GovernanceHost host, IProc proc, TimeProvider time, TextWriter output, IGitHubApi? gitHub = null)
{
    /// <summary>What <c>rig repo-governance</c> is.</summary>
    public const string Description = "CLI tool to audit, inspect status, and enforce repository conventions across GitHub repos.";

    private const string Rubric = "docs/agents/agent-consistency.md";

    /// <summary>The <c>repo-governance</c> group with its ported subcommands.</summary>
    public Command Build()
    {
        var governance = new Command("repo-governance", Description);
        governance.Subcommands.Add(AgentsAudit());
        governance.Subcommands.Add(Plan());
        governance.Subcommands.Add(SyncLabels());
        governance.Subcommands.Add(Environments());
        governance.Subcommands.Add(Pages());
        governance.Subcommands.Add(License());
        governance.Subcommands.Add(SyncTemplates());
        governance.Subcommands.Add(Gitignore());
        governance.Subcommands.Add(Apps());
        return governance;
    }

    private Command AgentsAudit()
    {
        var path = new Argument<string?>("path") { Description = "Target repository directory path (defaults to CWD)", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all", "-a") { Description = "Audit all personal repositories under <code root>/personal" };
        var json = new Option<bool>("--json") { Description = "Emit findings as JSON for an agent to act on" };
        var remote = new Option<bool>("--remote")
        {
            Description = "Also sweep default branches on GitHub, including repos with no local clone and archived ones (which are reported but do not fail the audit)",
        };
        var strict = new Option<bool>("--strict") { Description = "Fail on warnings too, not only errors" };
        var writeWaivers = new Option<bool>("--write-waivers") { Description = $"Append a stub for every surviving finding to {AgentsMdAudit.WaiverFileName}" };
        var command = new Command(
            "agents-audit",
            "Audits AGENTS.md and the docs tree against the machine-decidable rules of docs/agents/agent-consistency.md. A repo dismisses a "
            + "finding with a reasoned waiver in .agents-audit.json; --remote also sweeps each default branch on GitHub.")
        {
            path, all, json, remote, strict, writeWaivers,
        };
        command.SetAction((parsed, cancellationToken) => AgentsAuditAsync(
            new(parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(json), parsed.GetValue(remote), parsed.GetValue(strict), parsed.GetValue(writeWaivers)),
            cancellationToken));
        return command;
    }

    private async Task<int> AgentsAuditAsync(AuditOptions options, CancellationToken cancellationToken)
    {
        var policies = RepoPolicies.Load(host.PolicyFile);
        var scopes = EstateScopes.Load(host.ScopesFile);
        var git = new Git(proc, host.CodeRoot);
        var audit = new AgentsMdAudit(git, time);

        // `--remote` on its own means "GitHub only"; with a path or --all it sweeps both.
        IReadOnlyList<string> targets = options.Remote && !(!string.IsNullOrEmpty(options.Path) || options.All)
            ? []
            : await new GovernedRepos(proc, git, scopes, policies).ResolveTargetsAsync(options.Path, options.All, host.PersonalDir, host.Cwd, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<AgentsMdAuditResult>();
        foreach (var target in targets)
        {
            var key = await PolicyKeys.OfAsync(git, scopes, target, cancellationToken).ConfigureAwait(false);
            results.Add(await audit.AuditAsync(target, policies.AgentsMd(key), cancellationToken).ConfigureAwait(false));
        }

        var sweep = options.Remote
            ? await new RemoteAgentFiles(proc).SweepAsync(policies.AllowedOwners(string.Empty), policies.IsRegistered, cancellationToken).ConfigureAwait(false)
            : RemoteSweep.None;

        if (options.Json)
        {
            var payload = new JsonObject
            {
                ["results"] = new JsonArray([.. results.Select(JsonNode (r) => r.ToJson())]),
                ["remote"] = new JsonArray([.. sweep.Findings.Select(JsonNode (f) => f.ToJson())]),
                ["remote_unread"] = new JsonArray([.. sweep.Unread.Select(name => (JsonNode?)name)]),
                ["gating_checks"] = new JsonArray([.. AgentsMdAudit.GatingChecks.Select(name => (JsonNode?)name)]),
                ["report_only_checks"] = new JsonArray([.. AgentsMdAudit.ReportOnlyChecks.Select(name => (JsonNode?)name)]),
                ["rubric"] = Rubric,
            };
            await output.WriteLineAsync(PyJson.Dumps(payload)).ConfigureAwait(false);
        }
        else
        {
            await output.WriteAsync(RenderAudit(results)).ConfigureAwait(false);
            if (options.Remote)
            {
                await output.WriteAsync(RenderRemote(sweep)).ConfigureAwait(false);
            }
        }

        if (options.WriteWaivers)
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var written = await AppendWaiverStubsAsync(targets[i], results[i], cancellationToken).ConfigureAwait(false);
                if (written > 0)
                {
                    await output.WriteLineAsync(
                        $"{Path.GetFileName(Path.TrimEndingDirectorySeparator(targets[i]))}: wrote {written} stub(s) to {AgentsMdAudit.WaiverFileName} — "
                        + "fill in each `reason` or the audit stays red.").ConfigureAwait(false);
                }
            }
        }

        var failed = results.Any(r => r.Failed || (options.Strict && r.Findings.Count > 0))
            || sweep.Findings.Any(f => !f.Archived)
            || sweep.Unread.Count > 0;
        return failed ? 1 : 0;
    }

    // One table per repo with findings, then the report-only metrics; a clean repo gets one line.
    private static string RenderAudit(IReadOnlyList<AgentsMdAuditResult> results)
    {
        var text = new StringBuilder();
        foreach (var result in results)
        {
            if (result.Findings.Count == 0 && result.Suppressed.Count == 0)
            {
                _ = text.Append("✓ ").Append(result.Repo).Append(": clean\n");
                continue;
            }

            var table = new TextTable($"{result.Repo} — AGENTS.md audit", [("Check", false), ("Where", false), ("Finding", false)]);
            foreach (var finding in result.Findings)
            {
                _ = table.Add(finding.Check, finding.Line is { } line ? $"{finding.Path}:{line}" : finding.Path, finding.Message);
            }

            _ = text.Append(table.Render());
            if (result.Suppressed.Count > 0)
            {
                _ = text.Append("  ").Append(result.Suppressed.Count).Append(" finding(s) waived in ").Append(AgentsMdAudit.WaiverFileName).Append('\n');
            }

            _ = text.Append("  metrics (not gated): ").AppendJoin(", ", result.Metrics.Select(m => $"{m.Key} {m.Value}")).Append("\n\n");
        }

        return text.ToString();
    }

    // The GitHub sweep: anything unread fails; archived rows are reported, never gated.
    private static string RenderRemote(RemoteSweep sweep)
    {
        var text = new StringBuilder();
        foreach (var name in sweep.Unread)
        {
            _ = text.Append("✗ GitHub sweep could not read ").Append(name).Append(": check `gh auth status` and retry\n");
        }

        if (sweep.Findings.Count == 0)
        {
            return sweep.Unread.Count == 0 ? text.Append("✓ GitHub sweep: no redundant agent files\n").ToString() : text.ToString();
        }

        var table = new TextTable("GitHub default branches — redundant agent files", [("Repository", false), ("Path", false), ("State", false)]);
        foreach (var finding in sweep.Findings)
        {
            _ = table.Add(finding.Repo, finding.Path, finding.Archived ? "archived — left as-is" : "remove");
        }

        return text.Append(table.Render()).ToString();
    }

    // An unreasoned stub per surviving gating finding not already waived; how many were written.
    private static async Task<int> AppendWaiverStubsAsync(string repo, AgentsMdAuditResult result, CancellationToken cancellationToken)
    {
        var existing = AgentsMdAudit.LoadWaivers(repo);
        var known = existing.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        var additions = result.Findings
            .Where(f => !known.Contains(f.Key) && AgentsMdAudit.GatingChecks.Contains(f.Check, StringComparer.Ordinal))
            .Select(f => new AgentsMdWaiver(f.Check, f.Path))
            .ToList();
        if (additions.Count == 0)
        {
            return 0;
        }

        await File.WriteAllTextAsync(Path.Join(repo, AgentsMdAudit.WaiverFileName), AgentsMdAudit.RenderWaivers([.. existing, .. additions]), new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        return additions.Count;
    }

    private sealed record AuditOptions(string? Path, bool All, bool Json, bool Remote, bool Strict, bool WriteWaivers);
}
