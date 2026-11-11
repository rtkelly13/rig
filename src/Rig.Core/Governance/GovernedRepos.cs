using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Governance;

/// <summary>
/// Which local checkouts repo governance acts on. Code merely cloned is out of scope: whether a checkout is a fork,
/// and whether you may push to it, comes from GitHub, and a profile naming a repo opts it in. Ported from
/// <c>rig.repo_governance.repo_discovery</c>.
/// </summary>
/// <param name="proc">Runs <c>gh</c>.</param>
/// <param name="git">Runs git, for each checkout's origin.</param>
/// <param name="scopes">The estate's org folders.</param>
/// <param name="policies">The policy, for selection rules and registrations.</param>
public sealed class GovernedRepos(IProc proc, Git git, EstateScopes scopes, RepoPolicies policies)
{
    private static readonly TimeSpan IdentityTimeout = TimeSpan.FromSeconds(120);

    private readonly Dictionary<string, RepoIdentity> _identities = new(StringComparer.Ordinal);

    /// <summary>The clones in <paramref name="personalDir"/> and the org folders beside it, each folder's sorted by name.</summary>
    public IReadOnlyList<string> EstateClones(string personalDir)
    {
        var clones = new List<string>();
        foreach (var parent in scopes.RepoParentDirs(personalDir).Where(DocsTree.IsDir))
        {
            clones.AddRange(DocsTree.List(parent)
                .Order(DocsTree.Order)
                .Where(child => DocsTree.IsDir(child) && DocsTree.Exists(Path.Join(child, ".git"))));
        }

        return clones;
    }

    /// <summary>Whether <paramref name="repoPath"/> is in scope, and why not when it is not.</summary>
    public async Task<ManageDecision> IsManagedAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var identity = await IdentityAsync(repoPath, cancellationToken).ConfigureAwait(false);
        if (!identity.Resolved)
        {
            return new(false, "could not resolve origin");
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        return RepoSelection.ShouldManage(policies.SelectionRules(name), policies.IsRegistered(name), identity.IsFork, identity.Permission);
    }

    /// <summary>
    /// What gh says about the checkout's origin, asked once per checkout: a sweep asks more than once (the selection
    /// filter, then the command), and the answer cannot change mid-run. Unresolved when gh cannot say, and an
    /// unresolved answer is asked again rather than remembered, so one transient gh error does not drop a checkout.
    /// </summary>
    public async Task<RepoIdentity> IdentityAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        if (_identities.TryGetValue(repoPath, out var known))
        {
            return known;
        }

        var origin = (await git.RunAsync(repoPath, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var view = await proc.RunAsync(
            new ProcRequest(["gh", "repo", "view", .. origin.Length > 0 ? [origin] : Array.Empty<string>(), "--json", "nameWithOwner,viewerPermission,isFork"])
            {
                WorkingDirectory = repoPath,
                Timeout = IdentityTimeout,
            },
            cancellationToken).ConfigureAwait(false);
        var identity = Identity(view);
        if (identity.Resolved)
        {
            _identities[repoPath] = identity;
        }

        return identity;
    }

    /// <summary>
    /// The checkouts a command acts on: with <paramref name="all"/>, every managed estate clone; else
    /// <paramref name="path"/> resolved against <paramref name="cwd"/> (none when it does not exist); else
    /// <paramref name="cwd"/> itself.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResolveTargetsAsync(
        string? path, bool all, string personalDir, string cwd, string? org = null, CancellationToken cancellationToken = default)
    {
        if (all || (!string.IsNullOrEmpty(org) && string.IsNullOrEmpty(path)))
        {
            var targets = new List<string>();
            foreach (var clone in EstateClones(personalDir))
            {
                if (!string.IsNullOrEmpty(org) && !await InOrgAsync(clone, org, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if ((await IsManagedAsync(clone, cancellationToken).ConfigureAwait(false)).Managed)
                {
                    targets.Add(clone);
                }
            }

            return targets;
        }

        if (!string.IsNullOrEmpty(path))
        {
            var target = PathResolution.Resolve(Path.Combine(cwd, path));
            return DocsTree.Exists(target) ? [target] : [];
        }

        return !string.IsNullOrEmpty(org) && !await InOrgAsync(cwd, org, cancellationToken).ConfigureAwait(false) ? [] : [cwd];
    }

    /// <summary>
    /// The checkout's GitHub <c>owner/name</c>, from <c>gh repo view</c> on its origin; null when gh cannot say.
    /// </summary>
    public async Task<string?> GhRepoNameAsync(string repoPath, CancellationToken cancellationToken = default)
    {
        var origin = (await git.RunAsync(repoPath, ["remote", "get-url", "origin"], cancellationToken: cancellationToken).ConfigureAwait(false)).OutOrEmpty;
        var view = await proc.RunAsync(
            new ProcRequest(["gh", "repo", "view", .. origin.Length > 0 ? [origin] : Array.Empty<string>(), "--json", "nameWithOwner", "-q", ".nameWithOwner"])
            {
                WorkingDirectory = repoPath,
                Timeout = IdentityTimeout,
            },
            cancellationToken).ConfigureAwait(false);
        return view.Ok ? view.Out.Trim() : null;
    }

    /// <summary>
    /// <paramref name="org"/>'s unarchived repos from <c>gh repo list</c>; when that lists none, the repos the policy
    /// registers under it and the estate clones that belong to it.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverOrgReposAsync(string org, string personalDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(org);
        var repos = new List<string>();
        var listed = await proc.RunAsync(
            new ProcRequest(["gh", "repo", "list", org, "--no-archived", "--limit", "100", "--json", "nameWithOwner"]) { Timeout = IdentityTimeout },
            cancellationToken).ConfigureAwait(false);
        if (listed.Ok && GitHubCalls.Parse(listed.Out) is JsonArray rows)
        {
            repos.AddRange(rows.Select(row => GitHubCalls.Str(row, "nameWithOwner")).OfType<string>());
        }

        if (repos.Count > 0)
        {
            return repos;
        }

        repos.AddRange(policies.ReposForOrg(org).Select(name => name.Contains('/', StringComparison.Ordinal) ? name : $"{org}/{name}"));
        foreach (var clone in EstateClones(personalDir))
        {
            var name = Path.GetFileName(clone);
            if (OwnedBy(name, org) || policies.IsRepoInOrg(name, org))
            {
                var slug = await GhRepoNameAsync(clone, cancellationToken).ConfigureAwait(false) is { Length: > 0 } found ? found : $"{org}/{name}";
                if (!repos.Contains(slug))
                {
                    repos.Add(slug);
                }
            }
        }

        return repos;
    }

    // Whether a checkout belongs to `org`: by its GitHub owner, its policy's expected owner or its registration.
    private async Task<bool> InOrgAsync(string repoPath, string org, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(repoPath));
        var slug = await GhRepoNameAsync(repoPath, cancellationToken).ConfigureAwait(false);
        return (!string.IsNullOrEmpty(slug) && slug.StartsWith($"{org}/", StringComparison.OrdinalIgnoreCase))
            || OwnedBy(name, org)
            || policies.IsRepoInOrg(name, org);
    }

    private bool OwnedBy(string name, string org) =>
        policies.ExpectedOwner(name) is { } expected && string.Equals(expected, org, StringComparison.OrdinalIgnoreCase);

    // The viewer's permission and whether the repo is a fork, from `gh repo view --json`; null when it did not answer.
    private static RepoIdentity Identity(Ran view)
    {
        if (!view.Ok || string.IsNullOrWhiteSpace(view.Out))
        {
            return RepoIdentity.Unresolved;
        }

        try
        {
            return JsonNode.Parse(view.Out) is JsonObject fields
                && fields["nameWithOwner"] is { } name && name.GetValueKind() == JsonValueKind.String
                && fields["viewerPermission"] is { } permission && permission.GetValueKind() == JsonValueKind.String
                && fields["isFork"] is { } fork && fork.GetValueKind() is JsonValueKind.True or JsonValueKind.False
                ? new(name.GetValue<string>(), permission.GetValue<string>(), fork.GetValue<bool>())
                : RepoIdentity.Unresolved;
        }
        catch (JsonException)
        {
            return RepoIdentity.Unresolved;
        }
    }
}

/// <summary>What gh says about a checkout's origin.</summary>
/// <param name="Slug">Its <c>owner/name</c>; empty when gh could not say.</param>
/// <param name="Permission">Your permission on it, such as <c>ADMIN</c> or <c>READ</c>.</param>
/// <param name="IsFork">Whether it is a fork.</param>
public sealed record RepoIdentity(string Slug, string Permission, bool IsFork)
{
    /// <summary>The identity of an origin gh could not resolve.</summary>
    public static RepoIdentity Unresolved { get; } = new(string.Empty, string.Empty, false);

    /// <summary>Whether gh resolved the origin.</summary>
    public bool Resolved => Slug.Length > 0;
}
