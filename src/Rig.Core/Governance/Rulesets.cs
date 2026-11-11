using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>A repo's default branch and merge settings; also the body of the <c>PATCH repos/&lt;repo&gt;</c> that sets them.</summary>
/// <param name="DefaultBranch">The default branch.</param>
/// <param name="Merge">The merge settings.</param>
public sealed record RepoSettings(string DefaultBranch, MergeSettings Merge)
{
    /// <summary>The PATCH body, in pydantic's compact form.</summary>
    public string Body() => PyRepr.Json(new JsonObject
    {
        ["default_branch"] = DefaultBranch,
        ["delete_branch_on_merge"] = Merge.DeleteBranchOnMerge,
        ["allow_squash_merge"] = Merge.AllowSquashMerge,
        ["allow_merge_commit"] = Merge.AllowMergeCommit,
        ["allow_rebase_merge"] = Merge.AllowRebaseMerge,
        ["allow_auto_merge"] = Merge.AllowAutoMerge,
    });
}

/// <summary>A repo's settings as GitHub reports them; a missing merge flag is null.</summary>
/// <param name="DefaultBranch">The default branch.</param>
/// <param name="DeleteBranchOnMerge">Whether branches are deleted on merge.</param>
/// <param name="AllowSquashMerge">Whether squash merges are allowed.</param>
/// <param name="AllowMergeCommit">Whether merge commits are allowed.</param>
/// <param name="AllowRebaseMerge">Whether rebase merges are allowed.</param>
/// <param name="AllowAutoMerge">Whether auto-merge is allowed.</param>
/// <param name="Private">Whether the repo is private.</param>
public sealed record LiveRepoSettings(
    string DefaultBranch = "main",
    bool DeleteBranchOnMerge = false,
    bool? AllowSquashMerge = null,
    bool? AllowMergeCommit = null,
    bool? AllowRebaseMerge = null,
    bool AllowAutoMerge = false,
    bool Private = false);

/// <summary>Merge settings and default branch (<c>gh api repos/&lt;repo&gt;</c>).</summary>
public sealed class RepoSettingsResource : Resource<RepoSettings, LiveRepoSettings>
{
    /// <inheritdoc />
    public override string Name => "repo-settings";

    /// <summary>
    /// Each setting GitHub holds differently from <paramref name="desired"/>, as <c>name live -&gt; wanted</c>. GitHub
    /// silently ignores <c>allow_auto_merge</c> on private repos under Free plans, so it is not drift on a private repo.
    /// </summary>
    public static IReadOnlyList<string> Drift(RepoSettings desired, LiveRepoSettings live)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(live);
        var drift = new List<string>();
        var liveBranch = live.DefaultBranch is { Length: > 0 } branch ? branch : "main";
        if (liveBranch != desired.DefaultBranch)
        {
            drift.Add($"default_branch {liveBranch} -> {desired.DefaultBranch}");
        }

        Compare("delete_branch_on_merge", live.DeleteBranchOnMerge, desired.Merge.DeleteBranchOnMerge);
        Compare("allow_squash_merge", live.AllowSquashMerge, desired.Merge.AllowSquashMerge);
        Compare("allow_merge_commit", live.AllowMergeCommit, desired.Merge.AllowMergeCommit);
        Compare("allow_rebase_merge", live.AllowRebaseMerge, desired.Merge.AllowRebaseMerge);
        if (!live.Private)
        {
            Compare("allow_auto_merge", live.AllowAutoMerge, desired.Merge.AllowAutoMerge);
        }

        return drift;

        void Compare(string name, bool? have, bool want)
        {
            if (have != want)
            {
                drift.Add($"{name} {PyRepr.Value(have)} -> {PyRepr.Value(want)}");
            }
        }
    }

    /// <inheritdoc />
    public override RepoSettings? Desired(RepoPolicies policies, Target target)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(target);
        return new(policies.ExpectedBaseBranch(target.Slug), policies.RepoSettings(target.Slug));
    }

    /// <inheritdoc />
    public override async Task<LiveRepoSettings> ReadAsync(IGitHubApi api, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var route = $"repos/{target.Slug}";
        return await GitHubCalls.ReadJsonAsync(api, route, cancellationToken).ConfigureAwait(false) is JsonObject repo
            ? new(
                GitHubCalls.Str(repo, "default_branch") ?? "main",
                GitHubCalls.Bool(repo, "delete_branch_on_merge") ?? false,
                GitHubCalls.Bool(repo, "allow_squash_merge"),
                GitHubCalls.Bool(repo, "allow_merge_commit"),
                GitHubCalls.Bool(repo, "allow_rebase_merge"),
                GitHubCalls.Bool(repo, "allow_auto_merge") ?? false,
                GitHubCalls.Bool(repo, "private") ?? false)
            : throw new GitHubReadException($"GET {route}: unreadable response");
    }

    /// <inheritdoc />
    public override IReadOnlyList<Change> Diff(RepoSettings desired, LiveRepoSettings observed) =>
        Drift(desired, observed) is { Count: > 0 } drift ? [new(Name, "update", string.Join("; ", drift))] : [];

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ApplyAsync(IGitHubApi api, Target target, RepoSettings desired, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.Any(c => c.Kind == Change.Apply))
        {
            return [];
        }

        var ran = await GitHubCalls.SendAsync(api, "PATCH", $"repos/{target.Slug}", desired.Body(), cancellationToken).ConfigureAwait(false);
        return ran.Ok ? [] : [$"PATCH repos/{target.Slug}: {ran.Err.Trim()}"];
    }
}

/// <summary>The ruleset a repo should carry, and why it must not be written when the check-name guard refuses.</summary>
/// <param name="Payload">The body sent, as Python's <c>json.dumps</c> writes it.</param>
/// <param name="Rules">The payload's comparable shape.</param>
/// <param name="Refusal">Why it must not be written, or null.</param>
public sealed record DeclaredRuleset(string Payload, RulesetShape Rules, string? Refusal);

/// <summary>The managed ruleset as GitHub holds it. <see cref="Rules"/> is null when it does not exist yet.</summary>
/// <param name="Served">Whether GitHub serves rulesets for this repo's plan.</param>
/// <param name="Id">Its id, or null.</param>
/// <param name="Rules">Its comparable shape, or null.</param>
public sealed record LiveRuleset(bool Served = true, long? Id = null, RulesetShape? Rules = null);

/// <summary>The parts of a ruleset the drift check compares.</summary>
/// <param name="Enforcement">Its enforcement, or null.</param>
/// <param name="Target">What it targets, or null.</param>
/// <param name="Include">The branch refs it covers, or null when it has no conditions.</param>
/// <param name="Exclude">The branch refs it excludes, or null when it has no conditions.</param>
/// <param name="BypassActorCount">How many actors may bypass it.</param>
/// <param name="Rules">Each rule type and what it declares, in order.</param>
public sealed record RulesetShape(
    string? Enforcement,
    string? Target,
    IReadOnlyList<string>? Include,
    IReadOnlyList<string>? Exclude,
    int BypassActorCount,
    IReadOnlyList<KeyValuePair<string, string>> Rules)
{
    private static readonly string[] Parameters =
    [
        "strict_required_status_checks_policy",
        "required_approving_review_count",
        "dismiss_stale_reviews_on_push",
        "require_code_owner_review",
        "require_last_push_approval",
        "required_review_thread_resolution",
        "require_extra_approval_for_unattributed_changes",
    ];

    /// <summary>
    /// A ruleset's shape from its JSON; null when it is not one. Each rule's declaration is Python's <c>repr</c> of its
    /// sorted check contexts and declared parameters, or empty when it has no parameters, so it reads the same as before.
    /// </summary>
    public static RulesetShape? From(JsonNode? node)
    {
        if (node is not JsonObject ruleset)
        {
            return null;
        }

        IReadOnlyList<string>? include = null;
        IReadOnlyList<string>? exclude = null;
        if (ruleset["conditions"] is JsonObject conditions)
        {
            var refName = conditions["ref_name"] as JsonObject;
            include = Strings(refName?["include"]);
            exclude = Strings(refName?["exclude"]);
            if (include is null || exclude is null)
            {
                return null;
            }
        }
        else if (ruleset["conditions"] is not null)
        {
            return null;
        }

        var rules = new List<KeyValuePair<string, string>>();
        var bypass = ruleset["bypass_actors"] switch
        {
            null => 0,
            JsonArray actors => actors.Count,
            _ => -1,
        };
        if (bypass < 0 || ruleset["rules"] is not (null or JsonArray))
        {
            return null;
        }

        foreach (var rule in ruleset["rules"] as JsonArray ?? [])
        {
            if (rule is not JsonObject body || Declaration(body["parameters"]) is not { } declared)
            {
                return null;
            }

            // A repeated type keeps its first place and its last declaration, as a Python dict does.
            var type = GitHubCalls.Str(body, "type") ?? string.Empty;
            var at = rules.FindIndex(r => r.Key == type);
            if (at < 0)
            {
                rules.Add(new(type, declared));
            }
            else
            {
                rules[at] = new(type, declared);
            }
        }

        return new(GitHubCalls.Str(ruleset, "enforcement"), GitHubCalls.Str(ruleset, "target"), include, exclude, bypass, rules);
    }

    /// <summary>Where <paramref name="live"/> differs from this declared shape, one line each.</summary>
    public IReadOnlyList<string> DriftOf(RulesetShape live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var drift = new List<string>();
        if (live.Enforcement != "active")
        {
            drift.Add($"enforcement {(string.IsNullOrEmpty(live.Enforcement) ? "unknown" : live.Enforcement)}");
        }

        if (live.Target != Target)
        {
            drift.Add("ruleset must target branches");
        }

        if (!SameRefs(live.Include, Include) || !SameRefs(live.Exclude, Exclude))
        {
            drift.Add("branch conditions differ from declared coverage");
        }

        if (live.BypassActorCount > 0)
        {
            drift.Add("bypass actors can skip required conversation resolution");
        }

        var have = live.Rules.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        var declared = Rules.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (type, want) in Rules)
        {
            if (!have.TryGetValue(type, out var held))
            {
                drift.Add($"missing rule {type}");
            }
            else if (held != want)
            {
                drift.Add($"{type}: live {held} != declared {want}");
            }
        }

        drift.AddRange(live.Rules.Where(r => !declared.Contains(r.Key)).Select(r => $"undeclared rule {r.Key}"));
        return drift;
    }

    private static bool SameRefs(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);

    private static List<string>? Strings(JsonNode? node) => node switch
    {
        null => [],
        JsonArray items when items.All(i => i is JsonValue v && v.GetValueKind() == JsonValueKind.String) => [.. items.Select(i => i!.GetValue<string>())],
        _ => null,
    };

    // `repr((checks, *params))`, "" when the rule has no parameters, null when they are not readable.
    private static string? Declaration(JsonNode? parameters)
    {
        if (parameters is null)
        {
            return string.Empty;
        }

        if (parameters is not JsonObject declared)
        {
            return null;
        }

        var contexts = new List<string>();
        if (declared["required_status_checks"] is JsonArray checks)
        {
            foreach (var check in checks)
            {
                if (check is not JsonObject entry)
                {
                    return null;
                }

                contexts.Add(GitHubCalls.Str(entry, "context") ?? string.Empty);
            }
        }

        var values = new List<string> { PyRepr.List(contexts.Order(StringComparer.Ordinal)) };
        foreach (var name in Parameters)
        {
            values.Add(declared[name] switch
            {
                null => "None",
                JsonValue v when v.GetValueKind() is JsonValueKind.True => "True",
                JsonValue v when v.GetValueKind() is JsonValueKind.False => "False",
                JsonValue v when v.GetValueKind() is JsonValueKind.Number && v.TryGetValue(out long n) => PyRepr.Value(n),
                _ => "?",
            });
        }

        return "(" + string.Join(", ", values) + ")";
    }
}

/// <summary>The Main Branch Governance Ruleset, behind the check-name guard.</summary>
public sealed class RulesetResource : Resource<DeclaredRuleset, LiveRuleset>
{
    /// <summary>The managed ruleset's name.</summary>
    public const string RulesetName = "Main Branch Governance Ruleset";

    // What GitHub answers a rulesets call on a private repo whose plan does not serve them.
    private const string Unserved = "Upgrade to GitHub Pro or make this repository public";

    /// <inheritdoc />
    public override string Name => "ruleset";

    /// <summary>
    /// The ruleset <paramref name="repo"/> should carry, covering its own default branch. On a managed fork the stack tool
    /// rebuilds that branch on every rebaseline (docs/forks.md), so it is not protected against non-fast-forward pushes
    /// and needs no pull request.
    /// </summary>
    public static JsonObject Payload(RepoPolicies policies, string repo)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var isFork = policies.Fork(repo) is not null;
        var rules = new JsonArray { new JsonObject { ["type"] = "deletion" } };
        if (!isFork)
        {
            rules.Add(new JsonObject { ["type"] = "non_fast_forward" });
        }

        rules.Add(new JsonObject { ["type"] = "required_linear_history" });
        if (!isFork)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "pull_request",
                ["parameters"] = new JsonObject
                {
                    ["required_approving_review_count"] = 0,
                    ["dismiss_stale_reviews_on_push"] = true,
                    ["require_code_owner_review"] = false,
                    ["require_last_push_approval"] = false,
                    ["required_review_thread_resolution"] = true,

                    // State every parameter: GitHub defaults anything omitted, so an undeclared one is reset on every
                    // `enforce`. This one made design-system#69 unmergeable; see docs/repo-governance/rulesets.md.
                    ["require_extra_approval_for_unattributed_changes"] = false,
                },
            });
        }

        if (policies.RequiredChecks(repo) is { Count: > 0 } checks)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "required_status_checks",
                ["parameters"] = new JsonObject
                {
                    ["strict_required_status_checks_policy"] = true,
                    ["required_status_checks"] = new JsonArray([.. checks.Select(JsonNode (c) => new JsonObject { ["context"] = c })]),
                },
            });
        }

        return new JsonObject
        {
            ["name"] = RulesetName,
            ["target"] = "branch",
            ["enforcement"] = "active",
            ["bypass_actors"] = new JsonArray(),
            ["conditions"] = new JsonObject
            {
                ["ref_name"] = new JsonObject { ["include"] = new JsonArray($"refs/heads/{policies.ExpectedBaseBranch(repo)}"), ["exclude"] = new JsonArray() },
            },
            ["rules"] = rules,
        };
    }

    /// <summary>
    /// Why the ruleset must not be written, or null. A required check no <c>pull_request</c> job reports blocks every PR,
    /// and undoing it means editing the ruleset in the GitHub UI. See docs/repo-governance/required-checks.md.
    /// </summary>
    public static string? CheckNameRefusal(string repoPath, string repo, RepoPolicies policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var required = policies.RequiredChecks(repo);
        if (required.Count == 0)
        {
            return null;
        }

        var declared = CheckNames.DiscoverPrCheckNames(repoPath);
        if (declared.Count == 0)
        {
            return $"required checks {PyRepr.List(required)}: no pull_request workflow exists to report them";
        }

        var missing = required.Where(name => !declared.ContainsKey(name)).ToList();
        return missing.Count > 0
            ? $"required checks {PyRepr.List(missing)}: no pull_request job declares them (declared: {PyRepr.List(declared.Keys.Order(StringComparer.Ordinal))})"
            : null;
    }

    /// <summary>Reads the managed ruleset; throws <see cref="GitHubReadException"/> when GitHub cannot be read.</summary>
    public static async Task<LiveRuleset> ReadRulesetAsync(IGitHubApi api, string repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var route = $"repos/{repo}/rulesets";
        var listed = await api.CallAsync(ApiRequest.Get(route), cancellationToken).ConfigureAwait(false);
        if (!listed.Ok)
        {
            return listed.Err.Contains(Unserved, StringComparison.Ordinal)
                ? new LiveRuleset(Served: false)
                : throw new GitHubReadException($"GET {route}: {GitHubCalls.Reason(listed)}");
        }

        if (GitHubCalls.Parse(listed.Out) is not JsonArray rulesets || rulesets.Any(r => r is not JsonObject))
        {
            throw new GitHubReadException($"GET {route}: unreadable response");
        }

        var named = rulesets.Where(r => GitHubCalls.Str(r, "name") == RulesetName && GitHubCalls.Number(r, "id") is not null).ToList();
        if (named.Count == 0)
        {
            return new LiveRuleset();
        }

        var chosen = named.FirstOrDefault(r => GitHubCalls.Str(r, "enforcement") == "active") ?? named[0];
        var id = GitHubCalls.Number(chosen, "id")!.Value;
        var detailRoute = $"{route}/{id}";
        var rules = RulesetShape.From(await GitHubCalls.ReadJsonAsync(api, detailRoute, cancellationToken).ConfigureAwait(false))
            ?? throw new GitHubReadException($"GET {detailRoute}: unreadable response");
        return new LiveRuleset(Id: id, Rules: rules);
    }

    /// <inheritdoc />
    public override DeclaredRuleset? Desired(RepoPolicies policies, Target target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var payload = PyJson.DumpsLine(Payload(policies, target.Slug));

        // Read back from the text, as the live ruleset is: a built node holds its numbers as int, not as JSON.
        return new(payload, RulesetShape.From(JsonNode.Parse(payload))!, CheckNameRefusal(target.Path, target.Slug, policies));
    }

    /// <inheritdoc />
    public override Task<LiveRuleset> ReadAsync(IGitHubApi api, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return ReadRulesetAsync(api, target.Slug, cancellationToken);
    }

    /// <inheritdoc />
    public override IReadOnlyList<Change> Diff(DeclaredRuleset desired, LiveRuleset observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);
        if (!observed.Served)
        {
            return [new(Name, "unserved", "rulesets are not served for this private repo's plan", Change.Note)];
        }

        if (desired.Refusal is { } refusal)
        {
            return [new(Name, "refused", refusal, Change.Refused)];
        }

        if (observed.Rules is null)
        {
            return [new(Name, "create", $"{RulesetName} is missing")];
        }

        return desired.Rules.DriftOf(observed.Rules) is { Count: > 0 } drift
            ? [new(Name, "update", string.Join("; ", drift), Subject: PyRepr.Value(observed.Id))]
            : [];
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ApplyAsync(IGitHubApi api, Target target, DeclaredRuleset desired, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(changes);
        var failures = new List<string>();
        foreach (var change in changes.Where(c => c.Kind == Change.Apply))
        {
            // Replace a drifted ruleset in place, so it is never duplicated.
            var ran = string.IsNullOrEmpty(change.Subject)
                ? await GitHubCalls.SendAsync(api, "POST", $"repos/{target.Slug}/rulesets", desired.Payload, cancellationToken).ConfigureAwait(false)
                : await GitHubCalls.SendAsync(api, "PUT", $"repos/{target.Slug}/rulesets/{change.Subject}", desired.Payload, cancellationToken).ConfigureAwait(false);
            if (!ran.Ok)
            {
                failures.Add($"{change.Action} {RulesetName} on {target.Slug}: {ran.Err.Trim()}");
            }
        }

        return failures;
    }
}
