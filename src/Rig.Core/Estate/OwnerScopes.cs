using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

namespace Rig.Core.Estate;

/// <summary>Which side of the estate a GitHub owner belongs to.</summary>
public enum OwnerClass
{
    /// <summary>A personal account or org, named in the tracked <c>config/estate-scopes.json</c>.</summary>
    Personal,

    /// <summary>A work org, named in this machine's <c>tools.estate.work_orgs</c>.</summary>
    Work,

    /// <summary>Neither: someone else's repository, report-only everywhere.</summary>
    Other,
}

/// <summary>
/// This machine's role and the GitHub owners on each side: the personal ones from the tracked
/// <c>config/estate-scopes.json</c>, the work ones from <c>~/.workspace/config.json</c> (rig is public, so work names
/// never sit in it). The role picks which side scheduled tooling, such as the daily orchestrator, writes to.
/// </summary>
/// <param name="Role">This machine's role; <see cref="MachineRole.Personal"/> when none is set.</param>
/// <param name="RoleSet">Whether the role came from the machine config rather than the default.</param>
/// <param name="Personal">Personal GitHub accounts, then personal orgs, as written.</param>
/// <param name="Work">Work GitHub orgs, as written.</param>
public sealed record OwnerScopes(MachineRole Role, bool RoleSet, IReadOnlyList<string> Personal, IReadOnlyList<string> Work)
{
    /// <summary>The scopes from the tracked scopes file and this machine's config file.</summary>
    /// <param name="scopesFile">The rig checkout's <c>config/estate-scopes.json</c>.</param>
    /// <param name="machineConfigFile">This machine's <c>~/.workspace/config.json</c>.</param>
    public static OwnerScopes Load(string scopesFile, string machineConfigFile)
    {
        var personal = EstateScopes.Load(scopesFile);
        var role = MachineConfig.Role(machineConfigFile);
        return new(role ?? MachineRole.Personal, role is not null, [.. personal.Owners, .. personal.Orgs], MachineConfig.WorkOrgs(machineConfigFile));
    }

    /// <summary>The owners this machine's role may write to: the work orgs on a work machine, else the personal owners.</summary>
    public IReadOnlyList<string> Writable => Role == MachineRole.Work ? Work : Personal;

    /// <summary>The side <paramref name="owner"/> (any case) is on. Personal wins if an owner is listed on both.</summary>
    public OwnerClass Classify(string owner) =>
        Personal.Contains(owner, StringComparer.OrdinalIgnoreCase) ? OwnerClass.Personal
        : Work.Contains(owner, StringComparer.OrdinalIgnoreCase) ? OwnerClass.Work
        : OwnerClass.Other;
}

/// <summary>One GitHub owner found among the clones, with its side and whether this machine's role may write to it.</summary>
/// <param name="Owner">The owner as the first clone's origin spells it.</param>
/// <param name="Class">Its side of the estate.</param>
/// <param name="Writable">Whether this machine's role writes to it.</param>
/// <param name="Clones">The clones whose origin it owns, in the order found.</param>
public sealed record OwnerTally(string Owner, OwnerClass Class, bool Writable, IReadOnlyList<string> Clones);

/// <summary>What the clones under a root say about <see cref="OwnerScopes"/>: every owner they belong to, classified.</summary>
/// <param name="git">Reads each clone's origin.</param>
public sealed class OwnerCensus(Git git)
{
    /// <summary>Every GitHub owner of <paramref name="clones"/>, personal then work then other, most clones first; plus the clones with no GitHub origin.</summary>
    public async Task<(IReadOnlyList<OwnerTally> Owners, IReadOnlyList<string> Unowned)> CountAsync(
        OwnerScopes scopes, IReadOnlyList<string> clones, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clones);
        var origins = new PullRequests(git, NoGitHub.Instance);
        var byOwner = new Dictionary<string, (string Owner, List<string> Clones)>(StringComparer.OrdinalIgnoreCase);
        var unowned = new List<string>();
        foreach (var clone in clones)
        {
            if (await origins.OriginAsync(clone, cancellationToken).ConfigureAwait(false) is not { } slug)
            {
                unowned.Add(clone);
                continue;
            }

            if (!byOwner.TryGetValue(slug.Owner, out var entry))
            {
                entry = (slug.Owner, []);
                byOwner[slug.Owner] = entry;
            }

            entry.Clones.Add(clone);
        }

        var writable = new HashSet<string>(scopes.Writable, StringComparer.OrdinalIgnoreCase);
        var owners = byOwner.Values
            .Select(e => new OwnerTally(e.Owner, scopes.Classify(e.Owner), writable.Contains(e.Owner), e.Clones))
            .OrderBy(t => t.Class)
            .ThenByDescending(t => t.Clones.Count)
            .ThenBy(t => t.Owner, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return (owners, unowned);
    }

    // Origins are read from git alone; nothing here asks GitHub.
    private sealed class NoGitHub : IGitHubApi
    {
        public static readonly NoGitHub Instance = new();

        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The owner census does not call GitHub.");
    }
}
