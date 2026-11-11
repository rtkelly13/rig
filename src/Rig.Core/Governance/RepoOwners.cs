namespace Rig.Core.Governance;

/// <summary>Whether a repo's GitHub owner is the one its policy expects.</summary>
/// <param name="Status"><c>ALIGNED</c>, <c>WRONG_OWNER</c>, <c>UNAUTHORIZED_OWNER</c> or <c>UNKNOWN_OWNER</c>.</param>
/// <param name="IsAligned">Whether the owner is authorized.</param>
/// <param name="Message">Why, as the report prints it.</param>
public sealed record OwnerInspection(string Status, bool IsAligned, string Message);

/// <summary>Owner governance: a repo must live under its expected owner, or at least an allowed one. Ported from <c>repo_discovery.inspect_repo_owner</c>.</summary>
public static class RepoOwners
{
    /// <summary>
    /// Inspects <paramref name="slug"/> (<c>owner/name</c>) against the policy. The slug is the policy key: its owner lets
    /// an unregistered repo take its owner's profile, and its name is the remote's, not a renamed folder's.
    /// </summary>
    public static OwnerInspection Inspect(RepoPolicies policies, string? slug)
    {
        ArgumentNullException.ThrowIfNull(policies);
        if (string.IsNullOrEmpty(slug))
        {
            return new("UNKNOWN_OWNER", false, "Could not determine repository owner");
        }

        var slash = slug.IndexOf('/', StringComparison.Ordinal);
        var actual = slash < 0 ? null : slug[..slash];
        if (string.IsNullOrEmpty(actual))
        {
            return new("UNKNOWN_OWNER", false, $"Could not determine owner from repository slug '{slug}'");
        }

        var key = $"{actual}/{slug[(slash + 1)..]}";
        var expected = policies.ExpectedOwner(key);
        var allowed = policies.AllowedOwners(key);
        var inferred = policies.OwnerProfileFor(key);
        if (expected is not null && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return new("WRONG_OWNER", false, $"Expected owner '{expected}', but repo is owned by '{actual}'");
        }

        if (allowed.Count > 0 && !allowed.Contains(actual, StringComparer.OrdinalIgnoreCase))
        {
            return new("UNAUTHORIZED_OWNER", false, $"Owner '{actual}' is not in allowed owners ({string.Join(", ", allowed)})");
        }

        return new(
            "ALIGNED",
            true,
            inferred is null ? $"Owner '{actual}' is authorized" : $"Owner '{actual}' is authorized; not adopted: add it to profile {inferred}");
    }
}
