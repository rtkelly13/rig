using System.CommandLine;
using Rig.Cli.Vault;
using Rig.Core.Governance;
using Rig.Core.Processes;

namespace Rig.Cli.Governance;

/// <summary>
/// The checks over a checkout and its GitHub remote: <c>check-names</c> and <c>prune-branches</c>. Ported from
/// <c>rig.repo_governance.cli_repo_checks</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    private const string LexiconHint = "See 'repo-governance check-names --lexicon' for the preferred names.";

    private Command CheckNamesCommand()
    {
        var path = new Argument<string?>("path") { Description = "Target repository directory path (defaults to CWD)", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all", "-a") { Description = "Check all personal repositories under <code root>/personal" };
        var org = OrgOption();
        var strict = new Option<bool>("--strict") { Description = "Fail on badly-shaped names too, not just on names no job declares" };
        var lexicon = new Option<bool>("--lexicon") { Description = "Print the preferred check-name lexicon and exit" };
        var command = new Command(
            "check-names",
            "Verifies required check names exist in the workflows, and warns on names that will drift. Drift (a required check no "
            + "pull_request job declares) always fails; shape (a name outside the lexicon: lowercase snake_case, at most two words) "
            + "warns, and fails only under --strict. Renaming a required job means updating its required_checks in "
            + "config/repo_policies.json and re-running enforce in the same change.")
        {
            path, all, org, strict, lexicon,
        };
        command.SetAction((parsed, cancellationToken) => parsed.GetValue(lexicon)
            ? PrintLexiconAsync()
            : CheckNamesAsync(parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), parsed.GetValue(strict), cancellationToken));
        return command;
    }

    private async Task<int> PrintLexiconAsync()
    {
        var table = new TextTable("Preferred check names (lowercase snake_case, max 2 words)", [("Name", false), ("Use for", false)]);
        foreach (var (name, meaning) in CheckNames.PreferredCheckNames)
        {
            _ = table.Add(name, meaning);
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> CheckNamesAsync(string? path, bool all, string? org, bool strict, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Panel("Required Check Names: DRIFT & SHAPE")).ConfigureAwait(false);
        var (policies, repos) = Governed();
        var targets = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            await output.WriteLineAsync("❌ No target repositories found.").ConfigureAwait(false);
            return 2;
        }

        var git = new Git(proc, host.CodeRoot);
        var drifted = new HashSet<string>(StringComparer.Ordinal);
        var warned = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = 0;
        foreach (var target in targets)
        {
            var name = FolderName(target);
            if (await repos.GhRepoNameAsync(target, cancellationToken).ConfigureAwait(false) is not { Length: > 0 } repo)
            {
                // No origin is a local-only checkout; an origin gh cannot look up is a check that did not run.
                if ((await git.RunAsync(target, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty.Length == 0)
                {
                    await output.WriteLineAsync($"? {name}: no GitHub remote, skipped").ConfigureAwait(false);
                    continue;
                }

                unresolved++;
                await output.WriteLineAsync($"✗ {name}: gh could not look up its origin, not checked").ConfigureAwait(false);
                continue;
            }

            var result = CheckNames.Inspect(target, repo, policies);
            if (result.Status == "no_pr_workflows")
            {
                await output.WriteLineAsync($"· {name} ({repo}): no pull_request workflows, skipped").ConfigureAwait(false);
                continue;
            }

            if (result.Missing.Count > 0)
            {
                _ = drifted.Add(repo);
                await output.WriteLineAsync($"✗ {name} ({repo})").ConfigureAwait(false);
                foreach (var missing in result.Missing)
                {
                    await output.WriteLineAsync($"   required check {PyRepr.Str(missing)} is declared by no pull_request job").ConfigureAwait(false);
                }

                var declared = PyRepr.List(result.Declared.Keys.Order(StringComparer.Ordinal));
                await output.WriteLineAsync($"   declared: {declared}").ConfigureAwait(false);
                await output.WriteLineAsync($"   suggested required_checks: {declared}").ConfigureAwait(false);
            }
            else if (result.Warnings.Count > 0)
            {
                await output.WriteLineAsync($"⚠ {name} ({repo})").ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync($"✓ {name} ({repo}): {PyRepr.List(result.Required)}").ConfigureAwait(false);
            }

            foreach (var (check, warnings) in result.Warnings)
            {
                _ = warned.Add(repo);
                var where = result.Declared.GetValueOrDefault(check) is { Length: > 0 } at ? $" [{at}]" : string.Empty;
                await output.WriteLineAsync($"   {PyRepr.Str(check)}{where}").ConfigureAwait(false);
                foreach (var warning in warnings)
                {
                    await output.WriteLineAsync($"      - {warning}").ConfigureAwait(false);
                }
            }
        }

        await output.WriteLineAsync("\n" + new string('=', 50)).ConfigureAwait(false);
        if (drifted.Count > 0)
        {
            await output.WriteLineAsync($"FAILED: {drifted.Count} repo(s) require a check no job declares.").ConfigureAwait(false);
            await output.WriteLineAsync("Rename the job and update required_checks in config/repo_policies.json in the same change.").ConfigureAwait(false);
            return 1;
        }

        if (unresolved > 0)
        {
            await output.WriteLineAsync($"FAILED: {unresolved} repo(s) could not be looked up on GitHub.").ConfigureAwait(false);
            return 1;
        }

        if (warned.Count > 0)
        {
            await output.WriteLineAsync(strict
                ? $"FAILED (--strict): {warned.Count} repo(s) have badly-shaped names."
                : $"OK with warnings: {warned.Count} repo(s) have names that will drift.").ConfigureAwait(false);
            await output.WriteLineAsync(LexiconHint).ConfigureAwait(false);
            return strict ? 1 : 0;
        }

        await output.WriteLineAsync("SUCCESS: every required check is declared and well-named.").ConfigureAwait(false);
        return 0;
    }

    private Command PruneBranches()
    {
        var path = new Argument<string?>("path") { Description = "Target repository directory path (defaults to CWD)", Arity = ArgumentArity.ZeroOrOne };
        var all = new Option<bool>("--all", "-a") { Description = "Prune across all personal repositories" };
        var org = OrgOption();
        var apply = new Option<bool>("--apply") { Description = "Actually delete. Without this, reports only." };
        var showNoPr = new Option<bool>("--show-no-pr") { Description = "List branches that never had a PR (the default)" };
        var hideNoPr = new Option<bool>("--hide-no-pr") { Description = "Do not list branches that never had a PR" };
        var command = new Command(
            "prune-branches",
            "Deletes remote branches whose PR was merged. Dry run unless --apply. Merged-ness comes from GitHub, not git: under squash "
            + "merge a branch's commits never become ancestors of main. A branch with an open PR, no PR at all, or a protected name or "
            + "prefix is never deleted; repeat checkouts of one remote are pruned once.")
        {
            path, all, org, apply, showNoPr, hideNoPr,
        };
        command.SetAction((parsed, cancellationToken) => PruneBranchesAsync(
            parsed.GetValue(path), parsed.GetValue(all), parsed.GetValue(org), parsed.GetValue(apply), !parsed.GetValue(hideNoPr), cancellationToken));
        return command;
    }

    private async Task<int> PruneBranchesAsync(string? path, bool all, string? org, bool apply, bool showNoPr, CancellationToken cancellationToken)
    {
        var (policies, repos) = Governed();
        var targets = await repos.ResolveTargetsAsync(path, all, host.PersonalDir, host.Cwd, org, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            await output.WriteLineAsync("No target repositories found.").ConfigureAwait(false);
            return 0;
        }

        await output.WriteAsync(Panel($"Branch Cleanup: {(apply ? "APPLY" : "DRY RUN")}")).ConfigureAwait(false);
        var cleanup = new BranchCleanup(proc, new Git(proc, host.CodeRoot), repos, policies);
        var (deletable, deleted, failed) = (0, 0, 0);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var target in targets.Where(target => Path.Exists(Path.Join(target, ".git"))))
        {
            var plan = await cleanup.PlanAsync(target, cancellationToken).ConfigureAwait(false);
            if (plan.Skipped.Length > 0)
            {
                failed += plan.Unreadable ? 1 : 0;
                await output.WriteLineAsync($"- {plan.Repo}: skipped ({plan.Skipped})").ConfigureAwait(false);
                continue;
            }

            // Two checkouts of one remote produce identical plans; acting on both double-counts and works on stale refs.
            if (plan.Slug.Length > 0 && seen.TryGetValue(plan.Slug, out var first))
            {
                await output.WriteLineAsync($"- {plan.Repo}: same remote as {first} ({plan.Slug})").ConfigureAwait(false);
                continue;
            }

            if (plan.Slug.Length > 0)
            {
                seen[plan.Slug] = plan.Repo;
            }

            var merged = plan.Deletable;
            var noPr = showNoPr ? plan.Of("no-pr") : [];
            deletable += merged.Count;
            if (merged.Count == 0 && noPr.Count == 0)
            {
                await output.WriteLineAsync($"✓ {plan.Repo}: nothing to prune").ConfigureAwait(false);
                continue;
            }

            await output.WriteLineAsync($"\n{plan.Repo}").ConfigureAwait(false);
            foreach (var verdict in merged)
            {
                if (!apply)
                {
                    await output.WriteLineAsync($"  would delete {verdict.Branch} (PR #{verdict.Pr} merged)").ConfigureAwait(false);
                    continue;
                }

                var result = await cleanup.DeleteAsync(target, verdict.Branch, cancellationToken).ConfigureAwait(false);
                deleted += result.Deleted ? 1 : 0;
                failed += result.Deleted ? 0 : 1;
                await output.WriteLineAsync(result.Deleted
                    ? $"  ✗ deleted {verdict.Branch} (PR #{verdict.Pr})"
                    : $"  ! failed {verdict.Branch}: {result.Error}").ConfigureAwait(false);
            }

            if (noPr.Count > 0)
            {
                await output.WriteLineAsync($"  {noPr.Count} branch(es) with no PR — review manually, never auto-deleted:").ConfigureAwait(false);
                foreach (var verdict in noPr)
                {
                    await output.WriteLineAsync($"    · {verdict.Branch}").ConfigureAwait(false);
                }
            }
        }

        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync(
            apply ? $"Deleted {deleted}/{deletable} merged branch(es)."
            : deletable > 0 ? $"{deletable} merged branch(es) would be deleted. Re-run with --apply."
            : "No merged branches left behind.").ConfigureAwait(false);
        return failed > 0 ? 1 : 0;
    }
}
