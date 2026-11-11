using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>What GitHub reports for one environment today.</summary>
/// <param name="Name">The environment.</param>
/// <param name="WaitTimer">Minutes a job waits before it may deploy.</param>
/// <param name="Reviewers">
/// User reviewer logins, or null when GitHub does not serve protection rules for this repo's plan: "cannot tell" must
/// not collapse into "empty".
/// </param>
public sealed record CurrentEnvironment(string Name, long WaitTimer = 0, IReadOnlyList<string>? Reviewers = null);

/// <summary>One mutation, or one report-only finding, the environment plan decided on.</summary>
/// <param name="Kind"><c>create</c>, <c>wait-timer</c>, <c>add-reviewers</c>, <c>reviewers-unavailable</c> or <c>undeclared</c>.</param>
/// <param name="Environment">The environment.</param>
/// <param name="Detail">What exactly.</param>
public sealed record EnvironmentAction(string Kind, string Environment, string Detail = "")
{
    /// <summary>Whether this action writes to GitHub.</summary>
    public bool Mutates => Kind is "create" or "wait-timer" or "add-reviewers";
}

/// <summary>What <see cref="ReleaseEnvironments.SyncAsync"/> decided and the log of what it did about it.</summary>
/// <param name="Actions">What the plan decided.</param>
/// <param name="Log">A line per action.</param>
public sealed record EnvironmentSync(IReadOnlyList<EnvironmentAction> Actions, IReadOnlyList<string> Log);

/// <summary>
/// Audits and synchronizes GitHub deployment environments. On personal repos the <c>release</c> environment is
/// deliberately bare (required reviewers are org-only, and GitHub forbids self-approval); reviewer support exists for
/// the day a repo moves to an org with a second human. Ported from <c>rig.repo_governance.release_environments</c>.
/// </summary>
public static class ReleaseEnvironments
{
    /// <summary>Decides what to do. Pure: every fact is supplied by the caller.</summary>
    public static IReadOnlyList<EnvironmentAction> PlanActions(IReadOnlyList<EnvironmentPolicy> desired, IReadOnlyDictionary<string, CurrentEnvironment> current)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(current);
        var actions = new List<EnvironmentAction>();
        foreach (var policy in desired.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (!current.TryGetValue(policy.Name, out var env))
            {
                actions.Add(new("create", policy.Name, $"wait_timer={policy.WaitTimer}"));
                continue;
            }

            if (policy.WaitTimer != 0 && env.WaitTimer != policy.WaitTimer)
            {
                actions.Add(new("wait-timer", policy.Name, $"{env.WaitTimer} -> {policy.WaitTimer}"));
            }

            if (policy.Reviewers.Count > 0)
            {
                if (env.Reviewers is null)
                {
                    actions.Add(new("reviewers-unavailable", policy.Name, "GitHub does not serve protection rules for this repo's plan"));
                }
                else if (policy.Reviewers.Except(env.Reviewers, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
                {
                    actions.Add(new("add-reviewers", policy.Name, string.Join(", ", missing)));
                }
            }
        }

        var declared = desired.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        actions.AddRange(current.Keys.Where(name => !declared.Contains(name)).Order(StringComparer.Ordinal)
            .Select(name => new EnvironmentAction("undeclared", name, "exists on GitHub but no profile declares it")));
        return actions;
    }

    /// <summary>
    /// Every environment on <paramref name="repo"/>, tolerating unserved protection endpoints; throws
    /// <see cref="GitHubReadException"/> when the list itself cannot be read.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, CurrentEnvironment>> ReadAsync(IGitHubApi api, string repo, CancellationToken cancellationToken = default)
    {
        var route = $"repos/{repo}/environments";
        var listed = await GitHubCalls.ReadJsonAsync(api, route, cancellationToken).ConfigureAwait(false);
        if (listed is not JsonObject { } body || body["environments"] is not JsonArray entries
            || entries.Any(e => GitHubCalls.Str(e, "name") is null))
        {
            throw new GitHubReadException($"GET {route}: unreadable response");
        }

        var found = new Dictionary<string, CurrentEnvironment>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = GitHubCalls.Str(entry, "name")!;
            if (name.Length == 0)
            {
                continue;
            }

            found[name] = new(name, GitHubCalls.Number(entry, "wait_timer") ?? 0, await DeployReviewersAsync(api, repo, name, cancellationToken).ConfigureAwait(false));
        }

        return found;
    }

    /// <summary><see cref="ReadAsync"/>, reading an unreadable list as no environments.</summary>
    public static async Task<IReadOnlyDictionary<string, CurrentEnvironment>> FetchAsync(IGitHubApi api, string repo, CancellationToken cancellationToken = default)
    {
        try
        {
            return await ReadAsync(api, repo, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException)
        {
            return new Dictionary<string, CurrentEnvironment>();
        }
    }

    /// <summary>Applies the plan, or with <paramref name="dryRun"/> logs it; a log line per action.</summary>
    public static async Task<EnvironmentSync> SyncAsync(IGitHubApi api, string repo, IReadOnlyList<EnvironmentPolicy> desired, bool dryRun, CancellationToken cancellationToken = default)
    {
        var actions = PlanActions(desired, await FetchAsync(api, repo, cancellationToken).ConfigureAwait(false));
        var log = new List<string>();
        foreach (var action in actions)
        {
            if (!action.Mutates)
            {
                log.Add($"{action.Kind}: {action.Environment} — {action.Detail}");
            }
            else if (dryRun)
            {
                log.Add($"dry-run {action.Kind}: {action.Environment} — {action.Detail}");
            }
            else
            {
                log.Add(await ApplyAsync(api, repo, desired.First(p => p.Name == action.Environment), action, cancellationToken).ConfigureAwait(false));
            }
        }

        return new(actions, log);
    }

    /// <summary>Makes one mutating action; returns its log line, starting <c>FAILED</c> when it failed.</summary>
    internal static async Task<string> ApplyAsync(IGitHubApi api, string repo, EnvironmentPolicy policy, EnvironmentAction action, CancellationToken cancellationToken)
    {
        var route = $"repos/{repo}/environments/{action.Environment}";
        if (action.Kind is "create" or "wait-timer")
        {
            var ran = await GitHubCalls.SendAsync(api, "PUT", route, PyRepr.Json(new JsonObject { ["wait_timer"] = policy.WaitTimer }), cancellationToken).ConfigureAwait(false);
            var verb = action.Kind == "create" ? "created" : "updated wait_timer on";
            return ran.Ok ? $"{verb} {action.Environment}" : $"FAILED {action.Kind} {action.Environment}: {ran.Err}";
        }

        var reviewers = new JsonArray();
        foreach (var login in policy.Reviewers)
        {
            if (await UserIdAsync(api, login, cancellationToken).ConfigureAwait(false) is not { } id)
            {
                return $"FAILED add-reviewers {action.Environment}: cannot resolve user {login}";
            }

            reviewers.Add(new JsonObject { ["id"] = id, ["reviewer_type"] = "User" });
        }

        var patched = await GitHubCalls.SendAsync(api, "PATCH", $"{route}/deploy_reviewers", PyRepr.Json(new JsonObject { ["reviewers"] = reviewers }), cancellationToken)
            .ConfigureAwait(false);
        return patched.Ok ? $"configured reviewers on {action.Environment}" : $"FAILED add-reviewers {action.Environment}: {patched.Err}";
    }

    private static async Task<long?> UserIdAsync(IGitHubApi api, string login, CancellationToken cancellationToken)
    {
        try
        {
            return GitHubCalls.Number(await GitHubCalls.ReadJsonAsync(api, $"users/{login}", cancellationToken).ConfigureAwait(false), "id");
        }
        catch (GitHubReadException)
        {
            return null;
        }
    }

    // User reviewer logins, or null when GitHub does not serve the protection rules for this repo.
    private static async Task<IReadOnlyList<string>?> DeployReviewersAsync(IGitHubApi api, string repo, string environment, CancellationToken cancellationToken)
    {
        JsonNode served;
        try
        {
            served = await GitHubCalls.ReadJsonAsync(api, $"repos/{repo}/environments/{environment}/deploy_reviewers", cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException)
        {
            return null;
        }

        if (served is not JsonObject { } body || body["reviewers"] is not JsonArray entries || entries.Any(e => e is not JsonObject { } entry || entry["reviewer"] is not JsonObject))
        {
            return null;
        }

        return [.. entries.Select(e => GitHubCalls.Str(e!["reviewer"], "login")).OfType<string>().Where(login => login.Length > 0)];
    }
}

/// <summary>Deployment environments a profile declares (<c>environments</c> in <c>config/repo_policies.json</c>).</summary>
public sealed class EnvironmentsResource : Resource<IReadOnlyList<EnvironmentPolicy>, IReadOnlyDictionary<string, CurrentEnvironment>>
{
    /// <inheritdoc />
    public override string Name => "environments";

    /// <inheritdoc />
    public override IReadOnlyList<EnvironmentPolicy>? Desired(RepoPolicies policies, Target target)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(target);
        return policies.Environments(target.Slug) is { Count: > 0 } environments ? environments : null;
    }

    /// <inheritdoc />
    public override Task<IReadOnlyDictionary<string, CurrentEnvironment>> ReadAsync(IGitHubApi api, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ReleaseEnvironments.ReadAsync(api, target.Slug, cancellationToken);
    }

    /// <inheritdoc />
    public override IReadOnlyList<Change> Diff(IReadOnlyList<EnvironmentPolicy> desired, IReadOnlyDictionary<string, CurrentEnvironment> observed) =>
        [.. ReleaseEnvironments.PlanActions(desired, observed).Select(a => new Change(Name, a.Kind, a.Detail, a.Mutates ? Change.Apply : Change.Note, a.Environment))];

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ApplyAsync(
        IGitHubApi api, Target target, IReadOnlyList<EnvironmentPolicy> desired, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(changes);
        var failures = new List<string>();
        foreach (var change in changes.Where(c => c.Kind == Change.Apply))
        {
            var line = await ReleaseEnvironments.ApplyAsync(api, target.Slug, desired.First(p => p.Name == change.Subject), new(change.Action, change.Subject, change.Detail), cancellationToken)
                .ConfigureAwait(false);
            if (line.StartsWith("FAILED", StringComparison.Ordinal))
            {
                failures.Add(line);
            }
        }

        return failures;
    }
}
