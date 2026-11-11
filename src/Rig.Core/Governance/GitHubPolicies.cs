namespace Rig.Core.Governance;

/// <summary>The merge settings a repo carries on GitHub (<c>repo_settings</c>), set by one <c>PATCH repos/&lt;repo&gt;</c>.</summary>
/// <param name="DeleteBranchOnMerge">Delete a PR's branch once it merges.</param>
/// <param name="AllowSquashMerge">Allow squash merges.</param>
/// <param name="AllowMergeCommit">Allow merge commits.</param>
/// <param name="AllowRebaseMerge">Allow rebase merges.</param>
/// <param name="AllowAutoMerge">Allow auto-merge.</param>
public sealed record MergeSettings(
    bool DeleteBranchOnMerge = true,
    bool AllowSquashMerge = true,
    bool AllowMergeCommit = false,
    bool AllowRebaseMerge = false,
    bool AllowAutoMerge = true)
{
    /// <summary>Squash only, branches deleted on merge, auto-merge allowed.</summary>
    public static MergeSettings Default { get; } = new();
}

/// <summary>One label of the taxonomy (<c>labels</c>).</summary>
/// <param name="Name">Its name.</param>
/// <param name="Color">Six hex digits, without <c>#</c>.</param>
/// <param name="Description">Its description.</param>
public sealed record LabelSpec(string Name, string Color, string Description);

/// <summary>A declared deployment environment and its approval gate (<c>environments</c>).</summary>
/// <param name="Name">The key it sits under.</param>
/// <param name="WaitTimer">Minutes a job waits before it may deploy.</param>
/// <param name="Reviewers">Logins that must approve a job deploying to it.</param>
public sealed record EnvironmentPolicy(string Name, int WaitTimer, IReadOnlyList<string> Reviewers);

/// <summary>A repo's GitHub Pages policy (<c>pages</c>).</summary>
/// <param name="Enabled">Whether Pages should be on.</param>
/// <param name="BuildType"><c>workflow</c> or <c>legacy</c>.</param>
/// <param name="Cname">The custom domain, or null for the default one.</param>
/// <param name="HttpsEnforced">Whether HTTPS is enforced.</param>
public sealed record PagesPolicy(bool Enabled = false, string BuildType = "workflow", string? Cname = null, bool HttpsEnforced = true)
{
    /// <summary>Pages off.</summary>
    public static PagesPolicy Default { get; } = new();
}

/// <summary>A repo that tracks an upstream it does not own (<c>fork</c>); see docs/forks.md.</summary>
/// <param name="Upstream">The upstream's <c>owner/name</c>.</param>
/// <param name="UpstreamBranch">The upstream branch it follows.</param>
public sealed record ForkSpec(string Upstream, string UpstreamBranch = "main");
