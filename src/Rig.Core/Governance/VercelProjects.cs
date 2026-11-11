using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>One managed project's Vercel project names, with the GitHub <c>owner/repo</c> they deploy.</summary>
/// <param name="Project">The managed project.</param>
/// <param name="Slug">Its GitHub <c>owner/repo</c>.</param>
/// <param name="Names">The Vercel projects it declares.</param>
public sealed record VercelClaim(string Project, string Slug, IReadOnlyList<string> Names);

/// <summary>What Vercel says about one project: where it deploys from, and how that was found.</summary>
/// <param name="Name">The Vercel project.</param>
/// <param name="Source"><c>owner/repo</c> from its git link or its newest deployment's commit; null when neither.</param>
/// <param name="Via"><c>link</c>, <c>commit</c> or <c>none</c>.</param>
public sealed record Ownership(string Name, string? Source, string Via);

/// <summary>One ownership verdict.</summary>
/// <param name="VercelProject">The Vercel project.</param>
/// <param name="Severity"><c>error</c>, <c>warn</c> or <c>ok</c>.</param>
/// <param name="Rule">The verdict, such as <c>mismatch</c>.</param>
/// <param name="Detail">Why.</param>
/// <param name="Managed">The managed project it belongs to, when that is known.</param>
public sealed record OwnershipFinding(string VercelProject, string Severity, string Rule, string Detail, string? Managed = null);

/// <summary>The managed-project registry's <c>vercel</c> declarations is malformed.</summary>
public sealed class VercelClaimException : Exception
{
    /// <summary>A failure with no message.</summary>
    public VercelClaimException()
    {
    }

    /// <summary>A failure described by <paramref name="message"/>.</summary>
    public VercelClaimException(string message)
        : base(message)
    {
    }

    /// <summary>A failure described by <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public VercelClaimException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Which repository each Vercel project belongs to, checked against what Vercel says. Managed projects declare their
/// Vercel project names under <c>vercel</c> in <c>config/managed-projects.json</c>; a project's repository comes from
/// its git link, or for one deployed from the CLI, from the managed repo on GitHub holding its newest deployment's
/// commit. It reports; it never edits the config (ADR 0020). Ported from <c>rig.repo_governance.vercel_projects</c>
/// and the loaders of <c>rig.workspace.vercel_names</c>.
/// </summary>
public static partial class VercelProjects
{
    /// <summary>
    /// Every project's declared Vercel names in <paramref name="registry"/>; throws <see cref="VercelClaimException"/> for
    /// a malformed registry, a name that is not a Vercel name, or a name declared twice.
    /// </summary>
    public static IReadOnlyList<VercelClaim> LoadClaims(string registry)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var claims = new List<VercelClaim>();
        foreach (var project in Entries(registry))
        {
            if (project.Vercel.Count == 0)
            {
                continue;
            }

            var slug = RepoSlug.FromRemote(project.Repo)?.FullName
                ?? throw new VercelClaimException($"{project.Name}: vercel projects need a GitHub repo to belong to, not {PyRepr.Str(project.Repo)}");
            foreach (var name in project.Vercel)
            {
                if (!VercelName().IsMatch(name))
                {
                    throw new VercelClaimException($"{project.Name}: {PyRepr.Str(name)} is not a Vercel project name");
                }

                if (owners.TryGetValue(name, out var owner))
                {
                    throw new VercelClaimException($"{project.Name}: vercel project {PyRepr.Str(name)} is already declared by {owner}");
                }

                owners[name] = project.Name;
            }

            claims.Add(new(project.Name, slug, project.Vercel));
        }

        return claims;
    }

    /// <summary>Each enabled managed project's GitHub <c>owner/repo</c>, in registry order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> LoadRepoSlugs(string registry) =>
        [
            .. Entries(registry)
                .Where(p => p.Enabled)
                .Select(p => (p.Name, Slug: RepoSlug.FromRemote(p.Repo)?.FullName))
                .Where(p => p.Slug is not null)
                .Select(p => new KeyValuePair<string, string>(p.Name, p.Slug!)),
        ];

    /// <summary>Compares Vercel's facts with the declarations. Pure. <paramref name="repos"/> maps each managed project to its <c>owner/repo</c>.</summary>
    public static IReadOnlyList<OwnershipFinding> PlanFindings(
        IReadOnlyList<Ownership> owned, IReadOnlyList<VercelClaim> claims, IReadOnlyList<KeyValuePair<string, string>> repos)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(repos);
        var bySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        var byRepoName = new Dictionary<string, string>(StringComparer.Ordinal);
        var slugOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, slug) in repos)
        {
            bySlug[slug.ToLowerInvariant()] = name;
            byRepoName[RepoName(slug).ToLowerInvariant()] = name;
            slugOf[name] = slug;
        }

        var declared = new Dictionary<string, VercelClaim>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            foreach (var vercel in claim.Names)
            {
                declared[vercel] = claim;
            }
        }

        var findings = new List<OwnershipFinding>();
        foreach (var project in owned)
        {
            string? owner = null;
            if (project.Source is { } source && !bySlug.TryGetValue(source.ToLowerInvariant(), out owner)
                && byRepoName.TryGetValue(RepoName(source).ToLowerInvariant(), out owner))
            {
                findings.Add(new(project.Name, "warn", "stale-link", $"Vercel names {source}, which is now {slugOf[owner]}: relink the project", owner));
            }

            findings.Add(Verdict(project, declared.GetValueOrDefault(project.Name), owner));
        }

        var present = owned.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        findings.AddRange(
            declared.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Where(pair => !present.Contains(pair.Key))
                .Select(pair => new OwnershipFinding(pair.Key, "error", "missing", "declared, but there is no such Vercel project", pair.Value.Project)));
        return findings;
    }

    /// <summary>The managed <c>owner/repo</c> holding <paramref name="sha"/>, trying <paramref name="first"/>'s repo first.</summary>
    public static async Task<string?> CommitSourceAsync(
        string sha,
        IReadOnlyList<KeyValuePair<string, string>> repos,
        string? first,
        Func<string, string, CancellationToken, Task<bool>> contains,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repos);
        ArgumentNullException.ThrowIfNull(contains);
        var firstSlug = first is null ? null : repos.FirstOrDefault(pair => pair.Key == first).Value;
        foreach (var slug in repos.Select(pair => pair.Value).OrderBy(slug => slug != firstSlug))
        {
            if (await contains(slug, sha, cancellationToken).ConfigureAwait(false))
            {
                return slug;
            }
        }

        return null;
    }

    /// <summary>Whether GitHub's <paramref name="slug"/> has commit <paramref name="sha"/>.</summary>
    public static async Task<bool> HasCommitAsync(IGitHubApi gitHub, string slug, string sha, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gitHub);
        var ran = await gitHub.CallAsync(ApiRequest.Get($"repos/{slug}/commits/{sha}"), cancellationToken).ConfigureAwait(false);
        return ran.Ok && GitHubCalls.Parse(ran.Out) is JsonObject commit && commit["sha"] is JsonValue found
            && found.GetValueKind() == JsonValueKind.String && found.GetValue<string>() == sha;
    }

    /// <summary>Every Vercel project in the team, with the repository Vercel says it deploys from.</summary>
    public static async Task<IReadOnlyList<Ownership>> GatherAsync(
        VercelGating gating,
        IGitHubApi gitHub,
        string teamId,
        IReadOnlyList<VercelClaim> claims,
        IReadOnlyList<KeyValuePair<string, string>> repos,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gating);
        ArgumentNullException.ThrowIfNull(claims);
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            foreach (var vercel in claim.Names)
            {
                declared[vercel] = claim.Project;
            }
        }

        var owned = new List<Ownership>();
        foreach (var link in await gating.ListProjectLinksAsync(teamId, cancellationToken).ConfigureAwait(false))
        {
            if (link.Slug is not null)
            {
                owned.Add(new(link.Name, link.Slug, "link"));
                continue;
            }

            var sha = await gating.LatestCommitAsync(teamId, link.Name, cancellationToken).ConfigureAwait(false);
            var source = string.IsNullOrEmpty(sha)
                ? null
                : await CommitSourceAsync(sha, repos, declared.GetValueOrDefault(link.Name), (slug, commit, ct) => HasCommitAsync(gitHub, slug, commit, ct), cancellationToken)
                    .ConfigureAwait(false);
            owned.Add(new(link.Name, source, source is null ? "none" : "commit"));
        }

        return owned;
    }

    private static OwnershipFinding Verdict(Ownership project, VercelClaim? claim, string? owner)
    {
        var via = project.Via switch
        {
            "link" => "its git link",
            "commit" => "its deployed commit",
            _ => "nothing",
        };
        if (claim is null)
        {
            if (owner is not null)
            {
                return new(project.Name, "error", "undeclared", $"deploys from {owner} ({via}): declare it under {owner}", owner);
            }

            var source = string.IsNullOrEmpty(project.Source) ? string.Empty : $" ({project.Source}, not managed)";
            return new(project.Name, "error", "undeclared", $"owner unknown{source}: declare it");
        }

        if (owner is null)
        {
            return project.Source is null
                ? new(project.Name, "warn", "unverified", "no git link or commit: rests on the declaration", claim.Project)
                : new(project.Name, "error", "mismatch", $"declared under {claim.Project}, deploys from {project.Source}");
        }

        return owner != claim.Project
            ? new(project.Name, "error", "mismatch", $"declared under {claim.Project}, deploys from {owner} ({via})", owner)
            : new(project.Name, "ok", "ok", $"{owner}, confirmed by {via}", owner);
    }

    private static string RepoName(string slug) => slug[(slug.LastIndexOf('/') + 1)..];

    private sealed record Entry(string Name, string Repo, bool Enabled, IReadOnlyList<string> Vercel);

    // The registry's projects; a missing file, malformed JSON or a field of the wrong type throws.
    private static List<Entry> Entries(string registry)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(registry));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new VercelClaimException($"{registry}: {e.Message}", e);
        }

        if (root is not JsonObject file || (file["projects"] is { } listed && listed is not JsonArray))
        {
            throw new VercelClaimException($"{registry}: projects is not a list");
        }

        var entries = new List<Entry>();
        foreach (var node in file["projects"] as JsonArray ?? [])
        {
            if (node is not JsonObject entry
                || entry["name"] is not JsonValue name || name.GetValueKind() != JsonValueKind.String
                || entry["repo"] is not JsonValue repo || repo.GetValueKind() != JsonValueKind.String
                || entry["enabled"] is { } enabled && enabled.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)
                || entry["vercel"] is { } vercel && (vercel is not JsonArray names || names.Any(n => n?.GetValueKind() != JsonValueKind.String)))
            {
                throw new VercelClaimException($"{registry}: a project entry is misshapen");
            }

            entries.Add(new(
                name.GetValue<string>(),
                repo.GetValue<string>(),
                entry["enabled"]?.GetValue<bool>() ?? true,
                entry["vercel"] is JsonArray declared ? [.. declared.Select(n => n!.GetValue<string>())] : []));
        }

        return entries;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex VercelName();
}
