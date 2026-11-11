using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>One deployment as Vercel created it, in the audit window.</summary>
/// <param name="Ref">The branch it was built from; <c>?</c> when Vercel recorded none.</param>
/// <param name="Target"><c>production</c>, or null for a preview.</param>
/// <param name="State">Vercel's state, such as <c>READY</c> or <c>CANCELED</c>.</param>
/// <param name="CreatedAtMs">When it was created, in epoch milliseconds.</param>
/// <param name="CommitSha">The commit it was built from, when Vercel recorded one.</param>
public sealed record CreatedDeployment(string Ref, string? Target, string State, long CreatedAtMs, string? CommitSha = null);

/// <summary>Everything the gating audit knows about one Vercel project. Pure input.</summary>
/// <param name="Name">The Vercel project.</param>
/// <param name="Repo">The repository its git link names.</param>
/// <param name="CreateDeployments">Its <c>gitProviderOptions.createDeployments</c>: <c>enabled</c>, <c>disabled</c> or null (legacy).</param>
/// <param name="ProductionRef">Its production branch.</param>
/// <param name="Allow">The true entries of the repo's <c>git.deploymentEnabled</c> map; null when the repo declares no map.</param>
/// <param name="Deny">The false entries of that map.</param>
/// <param name="RecentDeployments">The deployments Vercel lists for it, newest first.</param>
/// <param name="HeadSha">The tip of <c>origin/main</c> in the local checkout, when there is one.</param>
public sealed record VercelProjectFacts(
    string Name,
    string? Repo,
    string? CreateDeployments,
    string ProductionRef,
    IReadOnlyList<string>? Allow,
    IReadOnlyList<string>? Deny,
    IReadOnlyList<CreatedDeployment> RecentDeployments,
    string? HeadSha = null);

/// <summary>One gating finding.</summary>
/// <param name="Project">The Vercel project.</param>
/// <param name="Rule">The rule, such as <c>creation-gate</c>.</param>
/// <param name="Severity"><c>error</c> or <c>warn</c>.</param>
/// <param name="Detail">What was found.</param>
public sealed record VercelFinding(string Project, string Rule, string Severity, string Detail);

/// <summary>One project's facts and what the audit found in them.</summary>
/// <param name="Facts">What was read.</param>
/// <param name="Findings">What was found.</param>
public sealed record VercelProjectAudit(VercelProjectFacts Facts, IReadOnlyList<VercelFinding> Findings);

/// <summary>A Vercel project and the GitHub <c>owner/repo</c> it deploys from; null for a CLI-deployed one.</summary>
/// <param name="Name">The Vercel project.</param>
/// <param name="Slug">Its git link's <c>owner/repo</c>.</param>
public sealed record VercelProjectLink(string Name, string? Slug);

/// <summary>
/// Vercel deployment-creation gating. Hobby's 100 deployments a day are spent when a deployment is created, so a
/// project must never create one for a push that cannot serve anything: either it does not create deployments on
/// branch pushes, or the repo's <c>vercel.json</c> declares a <c>git.deploymentEnabled</c> allowlist with a deny-all
/// fallback. Observed deployments back that up empirically. The judgement (<see cref="Plan"/>) is pure; reading
/// Vercel and the checkout is separate. Ported from <c>rig.repo_governance.vercel_gating</c>; see
/// docs/repo-governance/vercel-gating.md.
/// </summary>
/// <param name="api">The Vercel API.</param>
/// <param name="git">Reads the checkout's <c>origin/main</c>.</param>
public sealed class VercelGating(IVercelApi api, Git git)
{
    private const long DayMs = 86_400_000;

    /// <summary>Whether a deployment created from <paramref name="ref"/> is within budget etiquette.</summary>
    public static bool RefIsAllowed(string @ref, VercelProjectFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return @ref == facts.ProductionRef
            || (facts.Allow is { Count: > 0 } allow && allow.Any(pattern => Glob.Matches(@ref, pattern, OperatingSystem.IsWindows())));
    }

    /// <summary>
    /// Audits one project's facts, with no I/O. A creation finding is an error, because it spends the quota on every
    /// push; a freshness finding is a warning, because a quiet production branch is legitimately quiet.
    /// </summary>
    public static IReadOnlyList<VercelFinding> Plan(VercelProjectFacts facts, long nowMs, int windowDays)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var findings = new List<VercelFinding>();
        var windowMs = nowMs - (windowDays * DayMs);
        var recent = facts.RecentDeployments.Where(d => d.CreatedAtMs >= windowMs).ToList();

        // 1. The creation gate: `enabled` with no repo-level deny-all creates on every branch push.
        if (facts.CreateDeployments == "enabled" && !HasDenyAll(facts))
        {
            findings.Add(new(
                facts.Name,
                "creation-gate",
                "error",
                "creates a deployment for every branch push; ignored builds still count toward the 100/day. Add a git.deploymentEnabled "
                + "allowlist with a '**: false' fallback to the repo's vercel.json, or turn off deployment creation for branches in project settings."));
        }
        else if (facts.Allow is not null && !HasDenyAll(facts))
        {
            findings.Add(new(
                facts.Name,
                "allowlist-has-no-deny-all",
                "warn",
                "git.deploymentEnabled lists no deny-all fallback, so unlisted branches still create deployments by default."));
        }

        // 2. The empirical leak: whatever the flags say, deployments were created off the allowed refs.
        var leaked = recent.Where(d => !RefIsAllowed(d.Ref, facts)).ToList();
        if (leaked.Count > 0)
        {
            var listing = string.Join(
                ", ",
                leaked.GroupBy(d => d.Ref, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} ×{g.Count()}"));
            findings.Add(new(
                facts.Name,
                "budget-leak",
                "error",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"created {leaked.Count} deployment(s) off the allowed refs in the last {windowDays}d — each one burns the daily budget: {listing}")));
        }

        // 3. Production freshness: the served build is behind the branch it should serve.
        findings.AddRange(Freshness(facts, recent));
        return findings;
    }

    /// <summary>Every project in the team, by name, with the repository its git link names.</summary>
    public async Task<IReadOnlyList<VercelProjectLink>> ListProjectLinksAsync(string teamId, CancellationToken cancellationToken = default)
    {
        var projects = await ProjectsAsync(teamId, cancellationToken).ConfigureAwait(false);
        return
        [
            .. projects
                .Select(p => new VercelProjectLink(p.Name, p.Org is { Length: > 0 } org && p.Repo is { Length: > 0 } repo ? $"{org}/{repo}" : null))
                .OrderBy(link => link.Name, StringComparer.Ordinal),
        ];
    }

    /// <summary>The commit <paramref name="project"/>'s newest deployment was built from, from its git or CLI metadata.</summary>
    public async Task<string?> LatestCommitAsync(string teamId, string project, CancellationToken cancellationToken = default)
    {
        var raw = await api.GetAsync("/v6/deployments", [new("teamId", teamId), new("app", project), new("limit", "1")], cancellationToken).ConfigureAwait(false);
        if (Deployments(raw) is not [var newest, ..])
        {
            return null;
        }

        return newest.GitHubSha is { Length: > 0 } sha ? sha : newest.GitSha;
    }

    /// <summary>
    /// Reads each git-linked project's facts and plans them. <paramref name="onlyRepo"/> narrows to one repo's projects;
    /// <paramref name="reposDir"/> is where checkouts are looked for, by repo name.
    /// </summary>
    public async Task<IReadOnlyList<VercelProjectAudit>> AuditProjectsAsync(
        string teamId,
        int windowDays,
        long nowMs,
        string? reposDir = null,
        string? onlyRepo = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<VercelProjectAudit>();
        foreach (var project in await ProjectsAsync(teamId, cancellationToken).ConfigureAwait(false))
        {
            if (project.Repo is not { } repo || (!string.IsNullOrEmpty(onlyRepo) && repo != onlyRepo))
            {
                continue; // a CLI-deployed project has no pushes, so no budget burn
            }

            var raw = await api.GetAsync("/v6/deployments", [new("teamId", teamId), new("projectId", project.Id), new("limit", "100")], cancellationToken).ConfigureAwait(false);
            var deployments = Deployments(raw);
            var checkout = await CheckoutAsync(repo, project.RootDirectory, reposDir, cancellationToken).ConfigureAwait(false);
            var facts = new VercelProjectFacts(
                project.Name,
                repo,
                project.CreateDeployments,
                project.ProductionBranch is { Length: > 0 } production ? production : "main",
                checkout.Allow,
                checkout.Deny,
                [.. (deployments ?? []).Select(d => new CreatedDeployment(d.Ref is { Length: > 0 } branch ? branch : "?", d.Target, d.State, d.CreatedAt, d.GitHubSha))],
                checkout.HeadSha);
            var findings = Plan(facts, nowMs, windowDays).ToList();
            if (deployments is null)
            {
                findings.Add(new(
                    project.Name,
                    "deployments-unreadable",
                    "warn",
                    "Vercel /v6/deployments returned an unreadable response; deployment rules were not evaluated."));
            }

            results.Add(new(facts, findings));
        }

        return results;
    }

    /// <summary>
    /// The <c>git.deploymentEnabled</c> map in <paramref name="vercelJson"/>: a map's true and false entries, or a deny-all
    /// for <c>false</c>; both null when the file is unreadable or declares no map.
    /// </summary>
    public static (IReadOnlyList<string>? Allow, IReadOnlyList<string>? Deny) DeploymentMap(string vercelJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(vercelJson));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, null);
        }

        var enabled = root is JsonObject file && file["git"] is JsonObject gitConfig ? gitConfig["deploymentEnabled"] : null;
        return enabled switch
        {
            JsonObject entries => (
                [.. entries.Where(e => e.Value is JsonValue v && v.GetValueKind() == JsonValueKind.True).Select(e => e.Key)],
                [.. entries.Where(e => e.Value is JsonValue v && v.GetValueKind() == JsonValueKind.False).Select(e => e.Key)]),
            JsonValue v when v.GetValueKind() == JsonValueKind.False => ([], ["**"]),
            _ => (null, null),
        };
    }

    private static bool HasDenyAll(VercelProjectFacts facts) =>
        facts.Allow is not null && facts.Deny is { Count: > 0 } deny && deny.Any(pattern => pattern is "*" or "**");

    private static string Short(string sha) => sha[..Math.Min(7, sha.Length)];

    private static IEnumerable<VercelFinding> Freshness(VercelProjectFacts facts, List<CreatedDeployment> recent)
    {
        var ready = recent.Where(d => d.Target == "production" && d.State == "READY").ToList();
        if (facts.HeadSha is not { } head)
        {
            // No checkout to compare against; no production build at all in the window is still reportable.
            var pushes = recent.Count(d => d.Ref == facts.ProductionRef);
            if (pushes > 0 && ready.Count == 0)
            {
                yield return new(
                    facts.Name,
                    "production-stale",
                    "warn",
                    string.Create(CultureInfo.InvariantCulture, $"{pushes} push(es) to {facts.ProductionRef} in the window, none with a Ready production deployment."));
            }

            yield break;
        }

        if (ready.Count == 0)
        {
            yield return new(
                facts.Name,
                "production-stale",
                "warn",
                $"no Ready production deployment in the window while origin/{facts.ProductionRef} is at {Short(head)}.");
            yield break;
        }

        if (ready[0].CommitSha is { Length: > 0 } served && served != head)
        {
            yield return new(
                facts.Name,
                "production-stale",
                "warn",
                $"latest Ready production deployment is {Short(served)}, origin/{facts.ProductionRef} is {Short(head)} — production is behind.");
        }
    }

    // The team's projects; throws when the listing is unreadable, since nothing can be judged without it.
    private async Task<List<Project>> ProjectsAsync(string teamId, CancellationToken cancellationToken)
    {
        var raw = await api.GetAsync("/v9/projects", [new("teamId", teamId), new("limit", "100")], cancellationToken).ConfigureAwait(false);
        return Projects(raw) ?? throw new VercelApiException("Vercel /v9/projects returned an unreadable project list");
    }

    private async Task<(IReadOnlyList<string>? Allow, IReadOnlyList<string>? Deny, string? HeadSha)> CheckoutAsync(
        string repo, string? rootDirectory, string? reposDir, CancellationToken cancellationToken)
    {
        if (reposDir is null || Path.Join(reposDir, repo) is var checkout && !Directory.Exists(checkout))
        {
            return (null, null, null);
        }

        var (allow, deny) = DeploymentMap(Path.Join(checkout, rootDirectory ?? string.Empty, "vercel.json"));
        var head = (await git.RunAsync(checkout, ["rev-parse", "origin/main"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        return (allow, deny, head.Length > 0 ? head : null);
    }

    private sealed record Project(string Id, string Name, string? Repo, string? Org, string? RootDirectory, string? ProductionBranch, string? CreateDeployments);

    private sealed record Deployment(string State, long CreatedAt, string? Target, string? Ref, string? GitHubSha, string? GitSha);

    // The listing, or null when it or any project in it is misshapen.
    private static List<Project>? Projects(JsonNode? raw)
    {
        if (raw is not JsonObject body || body["projects"] is not JsonArray projects)
        {
            return null;
        }

        var read = new List<Project>();
        foreach (var node in projects)
        {
            if (node is not JsonObject p
                || !Required(p, "id", out var id) || !Required(p, "name", out var name)
                || !Optional(p, "rootDirectory", out var root) || !Optional(p, "productionBranch", out var branch)
                || !Nested(p, "link", out var link) || !Optional(link, "repo", out var repo) || !Optional(link, "org", out var org)
                || !Nested(p, "gitProviderOptions", out var options) || !Optional(options, "createDeployments", out var create))
            {
                return null;
            }

            read.Add(new(id, name, repo, org, root, branch, create));
        }

        return read;
    }

    // The deployments, or null when the response or any deployment in it is misshapen.
    private static List<Deployment>? Deployments(JsonNode? raw)
    {
        if (raw is not JsonObject body || body["deployments"] is not JsonArray deployments)
        {
            return null;
        }

        var read = new List<Deployment>();
        foreach (var node in deployments)
        {
            if (node is not JsonObject d
                || !Required(d, "state", out var state)
                || d["createdAt"] is not JsonValue created || created.GetValueKind() != JsonValueKind.Number || !created.TryGetValue<long>(out var createdAt)
                || !Optional(d, "target", out var target)
                || !Nested(d, "meta", out var meta) || !Optional(meta, "githubCommitRef", out var @ref) || !Optional(meta, "githubCommitSha", out var sha))
            {
                return null;
            }

            // A CLI deploy from a checkout records the commit but not the repo; a misshapen one is simply absent.
            var gitSha = meta?["gitCommitSha"] is JsonValue g && g.GetValueKind() == JsonValueKind.String ? g.GetValue<string>() : null;
            read.Add(new(state, createdAt, target, @ref, sha, gitSha));
        }

        return read;
    }

    private static bool Required(JsonObject owner, string key, out string value)
    {
        value = owner[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null!;
        return value is not null;
    }

    // An absent or null field reads as null; one of another type makes its owner misshapen.
    private static bool Optional(JsonObject? owner, string key, out string? value)
    {
        value = null;
        switch (owner?[key])
        {
            case null:
                return true;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                value = v.GetValue<string>();
                return true;
            default:
                return false;
        }
    }

    private static bool Nested(JsonObject owner, string key, out JsonObject? value)
    {
        value = owner[key] as JsonObject;
        return owner[key] is null or JsonObject;
    }
}
