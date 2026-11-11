using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Cli.Gh.Threads;
using Rig.Core.GitHub;
using Rig.Core.Governance;
using Rig.Core.Policy;
using Rig.Core.Processes;

namespace Rig.Cli.Governance;

/// <summary>
/// The triage dashboards and the merge preflight: <c>issue-triage</c>, <c>pr-triage</c> and <c>verify-pr-checks</c>.
/// Ported from <c>rig.repo_governance.cli_triage</c> and <c>review_threads</c>.
/// </summary>
internal sealed partial class GovernanceCommand
{
    // `mergeStateStatus` values GitHub would merge on; UNSTABLE means a check that is not required failed.
    private static readonly HashSet<string> Mergeable = new(["CLEAN", "HAS_HOOKS", "UNSTABLE"], StringComparer.Ordinal);

    // `gh pr checks` exits 8 while checks are still running.
    private const int ChecksPendingExit = 8;

    /// <summary>
    /// Whether every review thread on <paramref name="repo"/>#<paramref name="number"/> is resolved: human, bot and outdated
    /// threads alike. A failed or incomplete read blocks too.
    /// </summary>
    internal static async Task<ReviewThreadsResult> ReviewThreadsAsync(IGitHubApi api, string repo, int number, CancellationToken cancellationToken)
    {
        var parts = repo.Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length == 0) || number <= 0)
        {
            return new(false, $"Cannot verify review threads: invalid PR {repo}#{number.ToString(CultureInfo.InvariantCulture)}");
        }

        IReadOnlyList<ObservedThread> threads;
        try
        {
            threads = await new ThreadsGitHub(api).FetchThreadsAsync(new RepoSlug(parts[0], parts[1]), number, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubException ex)
        {
            return new(false, $"Failed to query review threads: {ex.Message}");
        }

        var unresolved = threads.Where(observed => !observed.Thread.Resolved).Select(observed => observed.Thread).ToList();
        if (unresolved.Count == 0)
        {
            return new(true, "All review threads are resolved");
        }

        var message = new StringBuilder($"{unresolved.Count.ToString(CultureInfo.InvariantCulture)} unresolved conversation thread(s)!\n");
        foreach (var thread in unresolved)
        {
            message.Append(CultureInfo.InvariantCulture, $"Thread {thread.Id} by @{thread.Author} is unresolved\n");
        }

        return new(false, message.Append("All conversation threads must be resolved before merging.").ToString());
    }

    private Command IssueTriage()
    {
        var (path, repo, org, all, json) = TriageOptions();
        var label = new Option<string?>("--label", "-l") { Description = "Filter issues by label" };
        var command = new Command("issue-triage", "Terminal dashboard for open issues across the estate categorized into actionable buckets.")
        {
            path, repo, org, label, all, json,
        };
        command.SetAction((parsed, cancellationToken) => IssueTriageAsync(
            new(parsed.GetValue(path), parsed.GetValue(repo), parsed.GetValue(org), parsed.GetValue(all), parsed.GetValue(json)), parsed.GetValue(label), cancellationToken));
        return command;
    }

    private Command PrTriage()
    {
        var (path, repo, org, all, json) = TriageOptions();
        var updateBehind = new Option<bool>("--update-behind") { Description = "Update branch for PRs behind main" };
        var mergeReady = new Option<bool>("--merge-ready") { Description = "Squash and merge PRs that are ready" };
        var command = new Command("pr-triage", "Terminal dashboard for open PRs with automated batch actions.")
        {
            path, repo, org, all, updateBehind, mergeReady, json,
        };
        command.SetAction((parsed, cancellationToken) => PrTriageAsync(
            new(parsed.GetValue(path), parsed.GetValue(repo), parsed.GetValue(org), parsed.GetValue(all), parsed.GetValue(json)),
            parsed.GetValue(updateBehind),
            parsed.GetValue(mergeReady),
            cancellationToken));
        return command;
    }

    private Command VerifyPrChecks()
    {
        var pr = new Argument<string>("pr") { Description = "PR number, branch name or URL" };
        var repo = RepoOption();
        var command = new Command(
            "verify-pr-checks",
            "Merge preflight: required checks pass, GitHub says the PR can merge, and every review thread is resolved. The checks are "
            + "GitHub's required ones (`gh pr checks --required`, set by the ruleset), and the merge state is GitHub's own `mergeStateStatus`.")
        {
            pr, repo,
        };
        command.SetAction((parsed, cancellationToken) => VerifyPrChecksAsync(parsed.GetValue(pr)!, parsed.GetValue(repo), cancellationToken));
        return command;
    }

    private async Task<int> IssueTriageAsync(TriageScope scope, string? label, CancellationToken cancellationToken)
    {
        var repos = await ReposToTriageAsync(scope, cancellationToken).ConfigureAwait(false);
        var triage = new Triage(proc);
        var issues = new List<IssueItem>();
        foreach (var repo in repos)
        {
            issues.AddRange(await triage.IssuesAsync(repo, label, cancellationToken).ConfigureAwait(false));
        }

        var dashboard = Triage.Dashboard(issues, time.GetUtcNow());
        if (scope.Json)
        {
            await output.WriteLineAsync(PyJson.Dumps(dashboard.ToJson())).ConfigureAwait(false);
            return 0;
        }

        var text = new StringBuilder(Panel($"Open Source Issue Triage: {dashboard.Total.ToString(CultureInfo.InvariantCulture)} Open Issues Across {repos.Count.ToString(CultureInfo.InvariantCulture)} Repos"));
        Section(text, "🚨 NEEDS TRIAGE (No type label and 0 responses)", dashboard[IssueBucket.NeedsTriage], issue => (issue.ShortRef, issue.Title + (issue.Author.Length > 0 ? $" by @{issue.Author}" : string.Empty), issue.Link));
        Section(text, "🐛 ACTIVE BUGS", dashboard[IssueBucket.ActiveBugs], issue => (issue.ShortRef, $"{issue.Title} [type: bug]", issue.Link));
        Section(text, "🙋 COMMUNITY / GOOD FIRST ISSUE", dashboard[IssueBucket.Community], issue =>
        {
            var tags = issue.LabelNames.Where(name => name.ToLowerInvariant() is "good first issue" or "help wanted").ToList();
            return (issue.ShortRef, issue.Title + (tags.Count > 0 ? $" [{string.Join(", ", tags)}]" : string.Empty), issue.Link);
        });
        Section(text, "💤 STALE WAITING ON REPRO (> 14 days)", dashboard[IssueBucket.StaleRepro], issue => (issue.ShortRef, $"{issue.Title} [status: needs-repro]", issue.Link));
        if (dashboard[IssueBucket.Other].Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nOther open issues (already triaged): {dashboard[IssueBucket.Other].Count}\n");
        }

        await output.WriteAsync(text.ToString()).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> PrTriageAsync(TriageScope scope, bool updateBehind, bool mergeReady, CancellationToken cancellationToken)
    {
        var repos = await ReposToTriageAsync(scope, cancellationToken).ConfigureAwait(false);
        var policies = RepoPolicies.Load(host.PolicyFile);
        var triage = new Triage(proc);
        var prs = new List<PrItem>();
        foreach (var repo in repos)
        {
            var required = policies.RequiredChecks(repo) is { Count: > 0 } registered ? registered : Triage.LegacyRequiredChecks;
            prs.AddRange(await triage.PullRequestsAsync(repo, required, cancellationToken).ConfigureAwait(false));
        }

        var dashboard = Triage.Dashboard(prs, time.GetUtcNow());
        if (scope.Json)
        {
            await output.WriteLineAsync(PyJson.Dumps(dashboard.ToJson())).ConfigureAwait(false);
            return 0;
        }

        var text = new StringBuilder(Panel($"Open Source Pull Request Triage: {dashboard.Total.ToString(CultureInfo.InvariantCulture)} Open PRs Across {repos.Count.ToString(CultureInfo.InvariantCulture)} Repos"));
        Section(text, "🟢 READY TO MERGE", dashboard[PrBucket.Ready], pr => (pr.ShortRef, $"{pr.Title} (branch: {pr.HeadBranch})", pr.Link));
        Section(text, "🟡 BEHIND MAIN", dashboard[PrBucket.Behind], pr => (pr.ShortRef, $"{pr.Title} (branch: {pr.HeadBranch})", pr.Link));
        Section(text, "🔴 BLOCKED / FAILING", dashboard[PrBucket.Blocked], pr => (pr.ShortRef, pr.Title + Tag(BlockedReasons(pr)), pr.Link));
        Section(text, "⏳ IN FLIGHT / PENDING", dashboard[PrBucket.Pending], pr => (pr.ShortRef, pr.Title + (pr.Pending.Count > 0 ? $" [pending: {string.Join(", ", pr.Pending)}]" : string.Empty), pr.Link));
        Section(text, "💤 STALE / IDLE (> 14 days)", dashboard[PrBucket.Stale], pr => (pr.ShortRef, pr.Title, pr.Link));
        await output.WriteAsync(text.ToString()).ConfigureAwait(false);

        if (updateBehind && dashboard[PrBucket.Behind].Count > 0)
        {
            await output.WriteLineAsync("\n── Updating behind branches ──").ConfigureAwait(false);
            foreach (var pr in dashboard[PrBucket.Behind])
            {
                await ReportAsync(await triage.UpdateBehindAsync(pr, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }

        if (mergeReady && dashboard[PrBucket.Ready].Count > 0)
        {
            await output.WriteLineAsync("\n── Merging ready PRs ──").ConfigureAwait(false);
            foreach (var pr in dashboard[PrBucket.Ready])
            {
                await ReportAsync(await triage.MergeReadyAsync(pr, (repo, number, ct) => ReviewThreadsAsync(Api, repo, number, ct), cancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }
        }

        return 0;
    }

    private async Task<int> VerifyPrChecksAsync(string pr, string? repo, CancellationToken cancellationToken)
    {
        var target = repo;
        if (string.IsNullOrEmpty(target))
        {
            var (_, repos) = Governed();
            target = await repos.GhRepoNameAsync(host.Cwd, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(target))
            {
                return await BlockedAsync("no GitHub repository found for the current directory; pass --repo").ConfigureAwait(false);
            }
        }

        var checks = await proc.RunAsync(new ProcRequest(["gh", "pr", "checks", pr, "--repo", target, "--required"]), cancellationToken).ConfigureAwait(false);

        // A repo whose ruleset requires no checks has nothing to wait for; the merge state still gates it.
        if (!checks.Ok && !checks.Err.Contains("no required checks", StringComparison.Ordinal))
        {
            var state = checks.Code == ChecksPendingExit ? "still pending" : "not passing";
            return await BlockedAsync($"required checks for PR {pr} in {target} are {state}", checks.Out.Length > 0 ? checks.Out : checks.Err).ConfigureAwait(false);
        }

        var view = await proc.RunAsync(new ProcRequest(["gh", "pr", "view", pr, "--repo", target, "--json", "number,mergeStateStatus"]), cancellationToken)
            .ConfigureAwait(false);
        if (MergeState(view) is not var (number, mergeState))
        {
            return await BlockedAsync($"could not read PR '{pr}' in {target}").ConfigureAwait(false);
        }

        if (!Mergeable.Contains(mergeState))
        {
            return await BlockedAsync($"PR {pr} in {target} cannot merge: mergeStateStatus is {mergeState}").ConfigureAwait(false);
        }

        var threads = await ReviewThreadsAsync(Api, target, number, cancellationToken).ConfigureAwait(false);
        if (!threads.IsAligned)
        {
            return await BlockedAsync($"PR {pr} in {target}: {threads.Message}").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"✓ Required checks pass, GitHub can merge it, and every conversation thread is resolved for PR {pr}!").ConfigureAwait(false);
        return 0;
    }

    private static (Argument<string?> Path, Option<string?> Repo, Option<string?> Org, Option<bool> All, Option<bool> Json) TriageOptions() =>
        (PathArgument(), RepoOption(), OrgOption(), AllOption(), new Option<bool>("--json") { Description = "Output triage findings as JSON" });

    private static Option<string?> RepoOption() => new("--repo", "-r") { Description = "Target GitHub repo (e.g. rtkelly13/design-system)" };

    // --repo alone; else --org alone lists the org on GitHub; else the targets' gh names, falling back to the org's repos.
    private async Task<IReadOnlyList<string>> ReposToTriageAsync(TriageScope scope, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(scope.Repo))
        {
            return [scope.Repo];
        }

        var (_, repos) = Governed();
        if (!string.IsNullOrEmpty(scope.Org) && string.IsNullOrEmpty(scope.Path) && !scope.All)
        {
            return await repos.DiscoverOrgReposAsync(scope.Org, host.PersonalDir, cancellationToken).ConfigureAwait(false);
        }

        var found = new List<string>();
        foreach (var target in await repos.ResolveTargetsAsync(scope.Path, scope.All, host.PersonalDir, host.Cwd, scope.Org, cancellationToken).ConfigureAwait(false))
        {
            if (await repos.GhRepoNameAsync(target, cancellationToken).ConfigureAwait(false) is { Length: > 0 } name && !found.Contains(name))
            {
                found.Add(name);
            }
        }

        if (!string.IsNullOrEmpty(scope.Org) && found.Count == 0)
        {
            found.AddRange(await repos.DiscoverOrgReposAsync(scope.Org, host.PersonalDir, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    private static void Section<T>(StringBuilder text, string heading, IReadOnlyList<T> items, Func<T, (string Ref, string Line, string Link)> describe)
    {
        text.Append(CultureInfo.InvariantCulture, $"\n{heading} ({items.Count})\n");
        if (items.Count == 0)
        {
            text.Append("  None\n");
        }

        foreach (var item in items)
        {
            var (reference, line, link) = describe(item);
            text.Append(CultureInfo.InvariantCulture, $"  • {reference}: {line}\n    {link}\n");
        }
    }

    private static List<string> BlockedReasons(PrItem pr)
    {
        var reasons = new List<string>();
        if (pr.IsDraft)
        {
            reasons.Add("draft");
        }

        if (pr.Mergeable == "CONFLICTING")
        {
            reasons.Add("conflict");
        }

        if (pr.Failed.Count > 0)
        {
            reasons.Add($"failing: {string.Join(", ", pr.Failed)}");
        }

        if (pr.Missing.Count > 0)
        {
            reasons.Add($"missing: {string.Join(", ", pr.Missing)}");
        }

        return reasons;
    }

    private static string Tag(List<string> parts) => parts.Count > 0 ? $" [{string.Join(", ", parts)}]" : string.Empty;

    private Task ReportAsync(PrActionResult result) => output.WriteLineAsync($"  {(result.Ok ? "✓" : "✗")} {result.Message}");

    private async Task<int> BlockedAsync(string message, string detail = "")
    {
        await output.WriteLineAsync($"BLOCKED: {message}").ConfigureAwait(false);
        if (detail.Trim() is { Length: > 0 } trimmed)
        {
            await output.WriteLineAsync(trimmed).ConfigureAwait(false);
        }

        return 1;
    }

    // `gh pr view --json number,mergeStateStatus`; null when gh failed or the answer lacks either field.
    private static (int Number, string State)? MergeState(Ran view)
    {
        if (!view.Ok)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(view.Out) is JsonObject row && row["number"] is JsonValue number && row["mergeStateStatus"] is JsonValue state
                ? (number.GetValue<int>(), state.GetValue<string>())
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record TriageScope(string? Path, string? Repo, string? Org, bool All, bool Json);
}
