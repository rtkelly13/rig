namespace Rig.Core.Governance;

/// <summary>Whether governance touches a repo at all (the <c>repo_selection</c> policy block).</summary>
/// <param name="ManageForks">Whether an unregistered fork is managed.</param>
/// <param name="RequirePushAccess">Whether an unregistered repo needs write access or better to be managed.</param>
/// <param name="Exempt">Whether the repos of the layer declaring it opt out of governance.</param>
public sealed record RepoSelectionRules(bool ManageForks = false, bool RequirePushAccess = true, bool Exempt = false)
{
    /// <summary>The rules with every field at its default.</summary>
    public static RepoSelectionRules Default { get; } = new();
}

/// <summary>Whether a repo is in scope, and why not when it is not.</summary>
/// <param name="Managed">Whether governance applies.</param>
/// <param name="Reason">Why it does not, or why a registered repo is exempt; empty otherwise.</param>
public sealed record ManageDecision(bool Managed, string Reason = "");

/// <summary>The decision whether governance touches a repo. Ported from <c>rig.repo_governance.repo_selection</c>.</summary>
public static class RepoSelection
{
    /// <summary>The GitHub permissions that count as push access.</summary>
    public static readonly IReadOnlySet<string> PushPermissions = new HashSet<string>(["ADMIN", "MAINTAIN", "WRITE"], StringComparer.Ordinal);

    /// <summary>
    /// Whether governance applies, from facts the caller supplies. A profile entry wins over every other rule, including
    /// one marking its repos exempt: listing a repo is a deliberate act.
    /// </summary>
    public static ManageDecision ShouldManage(RepoSelectionRules rules, bool registered, bool isFork, string permission)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(permission);
        if (registered)
        {
            return new(!rules.Exempt, rules.Exempt ? "exempt (its profile opts out of governance)" : string.Empty);
        }

        if (isFork && !rules.ManageForks)
        {
            return new(false, "fork (add it to a profile to manage it)");
        }

        if (rules.RequirePushAccess && !PushPermissions.Contains(permission.ToUpperInvariant()))
        {
            var shown = permission.Length > 0 ? permission : "unknown";
            return new(false, $"no push access ({shown.ToLowerInvariant()})");
        }

        return new(true);
    }
}
