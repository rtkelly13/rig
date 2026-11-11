using System.CommandLine;
using Rig.Core.Governance;
using Rig.Core.Processes;

namespace Rig.Cli.Governance;

/// <summary>
/// The policy files: <c>apply-policy</c> on one repo and <c>sync-policies</c> across the profiles that declare them.
/// Ported from <c>rig.repo_governance.cli_triage</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private Command ApplyPolicy()
    {
        var policy = new Option<string>("--policy", "-p") { Description = "Policy to apply: ai, security, or all", DefaultValueFactory = _ => "all" };
        var repo = new Option<string?>("--repo", "-r") { Description = "Target repo directory or name" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview changes without writing files" };
        var command = new Command(
            "apply-policy",
            "Applies parameterized governance policies (AI policy, security policy, README disclosure) to a target repo.")
        {
            policy, repo, dryRun,
        };
        command.SetAction((parsed, cancellationToken) => ApplyPolicyAsync(parsed.GetValue(policy)!, parsed.GetValue(repo), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private async Task<int> ApplyPolicyAsync(string policy, string? repo, bool dryRun, CancellationToken cancellationToken)
    {
        // A bare name that is not a folder here is looked for beside the working directory.
        var shown = repo ?? host.Cwd;
        var repoDir = Path.GetFullPath(shown, host.Cwd);
        var sibling = Path.Join(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(host.Cwd)), FolderName(repoDir));
        if (!Path.Exists(repoDir) && Path.Exists(sibling))
        {
            repoDir = sibling;
            shown = sibling;
        }

        if (!Path.Exists(repoDir) || !Path.Exists(Path.Join(repoDir, ".git")))
        {
            await output.WriteLineAsync($"❌ Directory {shown} is not a valid git repository.").ConfigureAwait(false);
            return 1;
        }

        var policies = RepoPolicies.Load(host.PolicyFile);
        var meta = await new PolicyFiles(proc, new Git(proc, host.CodeRoot)).DetectAsync(repoDir, policies, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(Panel($"Applying Governance Policies to {meta.RepoName}")).ConfigureAwait(false);
        await output.WriteLineAsync($"Visibility: {Visibility(meta)} | Runtimes: {meta.SupportedRuntimes}").ConfigureAwait(false);

        if (policy is ("ai" or "all"))
        {
            PolicyFiles.ApplyAiPolicy(repoDir, meta, dryRun);
            _ = PolicyFiles.InjectReadmeAiDisclosure(repoDir, meta, dryRun);
            await output.WriteLineAsync("  ✓ AI_POLICY.md and README.md disclosure applied").ConfigureAwait(false);
        }

        if (policy is ("security" or "all") && meta.IsPublic)
        {
            PolicyFiles.ApplySecurityPolicy(repoDir, meta, dryRun);
            await output.WriteLineAsync("  ✓ SECURITY.md applied").ConfigureAwait(false);
        }

        await output.WriteLineAsync("\n✓ Policy application complete!").ConfigureAwait(false);
        return 0;
    }

    private Command SyncPolicies()
    {
        var publicOnly = new Option<bool>("--public-only") { Description = "Only sync public open source profile repos (the default)" };
        var all = new Option<bool>("--all") { Description = "Sync every profile, not only public_open_source" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview changes without writing files" };
        var command = new Command("sync-policies", "Synchronizes governance policies across the profiles `config/repo_policies.json` declares.")
        {
            publicOnly, all, dryRun,
        };
        command.SetAction((parsed, cancellationToken) => SyncPoliciesAsync(!parsed.GetValue(all), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private async Task<int> SyncPoliciesAsync(bool publicOnly, bool dryRun, CancellationToken cancellationToken)
    {
        RepoPolicies policies;
        try
        {
            policies = RepoPolicies.Load(host.PolicyFile);
        }
        catch (InvalidDataException e)
        {
            await output.WriteLineAsync($"❌ Invalid policy config {host.PolicyFile}: {e.Message}").ConfigureAwait(false);
            return 1;
        }

        // The profiles name repos by folder: they sit in the working directory when it holds the estate, else beside it.
        var baseDir = Directory.Exists(Path.Join(host.Cwd, "shared-utilities"))
            ? host.Cwd
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(host.Cwd)) ?? host.Cwd;
        var files = new PolicyFiles(proc, new Git(proc, host.CodeRoot));
        foreach (var profile in policies.Profiles)
        {
            if (publicOnly && profile.Name != "public_open_source")
            {
                continue;
            }

            await output.WriteLineAsync($"\n── Syncing Profile: {profile.Name} ──").ConfigureAwait(false);
            foreach (var name in profile.Repos)
            {
                var target = Path.Join(baseDir, name);
                if (!Path.Exists(target))
                {
                    await output.WriteLineAsync($"  Skipping {name} (not found locally at {target})").ConfigureAwait(false);
                    continue;
                }

                var meta = await files.DetectAsync(target, policies, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"  • {name} ({Visibility(meta)})").ConfigureAwait(false);
                if (profile.Policies.Contains("ai_policy"))
                {
                    PolicyFiles.ApplyAiPolicy(target, meta, dryRun);
                    await output.WriteLineAsync("    ✓ AI_POLICY.md").ConfigureAwait(false);
                }

                if (profile.Policies.Contains("readme_ai_disclosure"))
                {
                    _ = PolicyFiles.InjectReadmeAiDisclosure(target, meta, dryRun);
                    await output.WriteLineAsync("    ✓ README.md AI Section").ConfigureAwait(false);
                }

                if (profile.Policies.Contains("security_policy"))
                {
                    PolicyFiles.ApplySecurityPolicy(target, meta, dryRun);
                    await output.WriteLineAsync("    ✓ SECURITY.md").ConfigureAwait(false);
                }
            }
        }

        await output.WriteLineAsync("\n✓ Multi-repo policy synchronization complete!").ConfigureAwait(false);
        return 0;
    }

    private static string Visibility(RepoMetadata meta) => meta.IsPublic ? "Public" : "Private";
}
