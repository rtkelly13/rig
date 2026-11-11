using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>What GitHub reports for one environment today.</summary>
/// <param name="Name">The environment.</param>
/// <param name="WaitTimer">Minutes a job waits before it may deploy.</param>
/// <param name="Reviewers">User reviewer logins from its <c>required_reviewers</c> protection rule; null reads as none.</param>
public sealed record CurrentEnvironment(string Name, long WaitTimer = 0, IReadOnlyList<string>? Reviewers = null);

/// <summary>One mutation, or one report-only finding, the environment plan decided on.</summary>
/// <param name="Kind"><c>create</c>, <c>wait-timer</c>, <c>add-reviewers</c> or <c>undeclared</c>.</param>
/// <param name="Environment">The environment.</param>
/// <param name="Detail">What exactly.</param>
public sealed record EnvironmentAction(string Kind, string Environment, string Detail = "")
{
    /// <summary>Whether this action writes to GitHub.</summary>
    public bool Mutates => Kind is "create" or "wait-timer" or "add-reviewers";
}

/// <summary>What <see cref="ReleaseEnvironments.SyncAsync"/> decided and the log of what it did about it.</summary>
/// <param name="Actions">What the plan decided.</param>
/// <param name="Log">A line per report-only or dry-run action, and one per environment written.</param>
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

            if (policy.Reviewers.Except(env.Reviewers ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
            {
                actions.Add(new("add-reviewers", policy.Name, string.Join(", ", missing)));
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

            found[name] = new(name, WaitTimer(entry), ReviewerLogins(entry));
        }

        return found;
    }

    /// <summary>
    /// Applies the plan, or with <paramref name="dryRun"/> logs it. Throws <see cref="GitHubReadException"/> when the
    /// environments cannot be read.
    /// </summary>
    public static async Task<EnvironmentSync> SyncAsync(IGitHubApi api, string repo, IReadOnlyList<EnvironmentPolicy> desired, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var actions = PlanActions(desired, await ReadAsync(api, repo, cancellationToken).ConfigureAwait(false));
        var log = new List<string>();
        foreach (var action in actions.Where(a => !a.Mutates || dryRun))
        {
            log.Add(action.Mutates ? $"dry-run {action.Kind}: {action.Environment} — {action.Detail}" : $"{action.Kind}: {action.Environment} — {action.Detail}");
        }

        if (!dryRun)
        {
            foreach (var group in actions.Where(a => a.Mutates).GroupBy(a => a.Environment, StringComparer.Ordinal))
            {
                log.Add(await ApplyAsync(api, repo, desired.First(p => p.Name == group.Key), [.. group.Select(a => a.Kind)], cancellationToken).ConfigureAwait(false));
            }
        }

        return new(actions, log);
    }

    /// <summary>
    /// Writes one environment in a single PUT, which replaces its protection rules: an undeclared wait timer and every
    /// reviewer already there are carried over, and the declared ones added. Returns its log line, starting
    /// <c>FAILED</c> when it failed.
    /// </summary>
    internal static async Task<string> ApplyAsync(IGitHubApi api, string repo, EnvironmentPolicy policy, IReadOnlyList<string> kinds, CancellationToken cancellationToken)
    {
        var route = $"repos/{repo}/environments/{policy.Name}";
        var what = string.Join(", ", kinds);
        JsonNode? existing = null;
        if (!kinds.Contains("create"))
        {
            try
            {
                existing = await GitHubCalls.ReadJsonAsync(api, route, cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubReadException e)
            {
                return $"FAILED {what} {policy.Name}: {e.Message}";
            }
        }

        var rule = ReviewersRule(existing);
        var reviewers = new JsonArray();
        foreach (var reviewer in rule?["reviewers"] as JsonArray ?? [])
        {
            if (GitHubCalls.Str(reviewer, "type") is { } type && GitHubCalls.Number(reviewer?["reviewer"], "id") is { } id)
            {
                reviewers.Add(new JsonObject { ["type"] = type, ["id"] = id });
            }
        }

        foreach (var login in policy.Reviewers.Except(ReviewerLogins(existing), StringComparer.Ordinal))
        {
            if (await UserIdAsync(api, login, cancellationToken).ConfigureAwait(false) is not { } id)
            {
                return $"FAILED {what} {policy.Name}: cannot resolve user {login}";
            }

            reviewers.Add(new JsonObject { ["type"] = "User", ["id"] = id });
        }

        var body = new JsonObject { ["wait_timer"] = policy.WaitTimer != 0 ? policy.WaitTimer : WaitTimer(existing) };
        if (reviewers.Count > 0)
        {
            body["reviewers"] = reviewers;
        }

        if (rule?["prevent_self_review"] is JsonValue preventSelfReview && preventSelfReview.TryGetValue<bool>(out var prevent))
        {
            body["prevent_self_review"] = prevent;
        }

        var ran = await GitHubCalls.SendAsync(api, "PUT", route, PyRepr.Json(body), cancellationToken).ConfigureAwait(false);
        return !ran.Ok ? $"FAILED {what} {policy.Name}: {ran.Err}"
            : kinds.Contains("create") ? $"created {policy.Name}"
            : $"updated {policy.Name}: {what}";
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

    // GitHub serves the wait timer as a protection rule, not a field of the environment.
    private static long WaitTimer(JsonNode? environment) =>
        environment?["protection_rules"] is JsonArray rules
            ? rules.Where(r => GitHubCalls.Str(r, "type") == "wait_timer").Select(r => GitHubCalls.Number(r, "wait_timer")).FirstOrDefault(t => t is not null) ?? 0
            : 0;

    private static JsonNode? ReviewersRule(JsonNode? environment) =>
        (environment?["protection_rules"] as JsonArray ?? []).FirstOrDefault(r => GitHubCalls.Str(r, "type") == "required_reviewers");

    // The user reviewers' logins; a team reviewer has no login and is left to whoever added it.
    private static List<string> ReviewerLogins(JsonNode? environment) =>
        [.. (ReviewersRule(environment)?["reviewers"] as JsonArray ?? [])
            .Where(r => GitHubCalls.Str(r, "type") == "User")
            .Select(r => GitHubCalls.Str(r?["reviewer"], "login"))
            .OfType<string>()
            .Where(login => login.Length > 0)];
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
        foreach (var group in changes.Where(c => c.Kind == Change.Apply).GroupBy(c => c.Subject, StringComparer.Ordinal))
        {
            var line = await ReleaseEnvironments.ApplyAsync(api, target.Slug, desired.First(p => p.Name == group.Key), [.. group.Select(c => c.Action)], cancellationToken)
                .ConfigureAwait(false);
            if (line.StartsWith("FAILED", StringComparison.Ordinal))
            {
                failures.Add(line);
            }
        }

        return failures;
    }
}
