using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>A repo's GitHub Pages site as GitHub reports it.</summary>
/// <param name="Cname">Its custom domain, or null.</param>
/// <param name="BuildType"><c>workflow</c> or <c>legacy</c>, or null.</param>
/// <param name="HttpsEnforced">Whether HTTPS is enforced.</param>
public sealed record LivePages(string? Cname = null, string? BuildType = null, bool HttpsEnforced = false);

/// <summary>A repo's Pages against its policy.</summary>
/// <param name="Enabled">Whether Pages is on.</param>
/// <param name="Cname">Its custom domain, or null.</param>
/// <param name="IsAligned">Whether it matches the policy.</param>
/// <param name="DriftReasons">Why not.</param>
/// <param name="Error">Why it could not be read, or null.</param>
public sealed record PagesInspection(bool Enabled, string? Cname, bool IsAligned, IReadOnlyList<string> DriftReasons, string? Error = null);

/// <summary>What aligning a repo's Pages did.</summary>
/// <param name="Status"><c>aligned</c>, <c>dry_run</c>, <c>success</c> or <c>error</c>.</param>
/// <param name="Actions">The writes planned or made, described.</param>
/// <param name="Error">Why a write failed, or null.</param>
public sealed record PagesAlignResult(string Status, IReadOnlyList<string> Actions, string? Error = null);

/// <summary>
/// GitHub Pages governance: build type, custom domain and HTTPS enforcement through the GitHub API. Ported from
/// <c>rig.repo_governance.pages_manager</c>.
/// </summary>
public static class PagesGovernance
{
    /// <summary>The repo's Pages site, or null when Pages is off (GitHub's 404); throws when it cannot be read.</summary>
    public static async Task<LivePages?> ReadAsync(IGitHubApi api, string repo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        var route = $"repos/{repo}/pages";
        var ran = await api.CallAsync(ApiRequest.Get(route), cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            return GitHubCalls.IsNotFound(ran) ? null : throw new GitHubReadException($"GET {route}: {GitHubCalls.Reason(ran)}");
        }

        return GitHubCalls.Parse(ran.Out) is JsonObject pages
            ? new(GitHubCalls.Str(pages, "cname"), GitHubCalls.Str(pages, "build_type"), GitHubCalls.Bool(pages, "https_enforced") ?? false)
            : throw new GitHubReadException($"GET {route}: unreadable response");
    }

    /// <summary>Why <paramref name="pages"/> (null when Pages is off) does not match <paramref name="expected"/>; empty when it does.</summary>
    public static IReadOnlyList<string> Drift(PagesPolicy? expected, LivePages? pages)
    {
        if (pages is null)
        {
            return expected is { Enabled: true } ? ["Pages is disabled"] : [];
        }

        if (expected is null)
        {
            return [];
        }

        if (!expected.Enabled)
        {
            return ["Pages is enabled, expected disabled"];
        }

        var drift = new List<string>();
        if (pages.BuildType != expected.BuildType)
        {
            drift.Add($"build_type is '{PyRepr.Value(pages.BuildType)}', expected '{expected.BuildType}'");
        }

        if (!string.IsNullOrEmpty(expected.Cname) && pages.Cname != expected.Cname)
        {
            drift.Add($"cname is '{PyRepr.Value(pages.Cname)}', expected '{expected.Cname}'");
        }

        if (expected.HttpsEnforced && !pages.HttpsEnforced)
        {
            drift.Add("https_enforced is False, expected True");
        }

        return drift;
    }

    /// <summary>Inspects <paramref name="repo"/>'s Pages against <paramref name="expected"/>.</summary>
    public static async Task<PagesInspection> InspectAsync(IGitHubApi api, string repo, PagesPolicy? expected, CancellationToken cancellationToken = default)
    {
        LivePages? pages;
        try
        {
            pages = await ReadAsync(api, repo, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubReadException e)
        {
            return new(false, null, false, [], e.Message);
        }

        var drift = Drift(expected, pages);
        return pages is null
            ? new(false, null, expected is { Enabled: false }, drift)
            : new(true, pages.Cname, drift.Count == 0, drift);
    }

    /// <summary>Enables Pages with the policy's build type when it is off, then configures its domain and HTTPS.</summary>
    public static async Task<PagesAlignResult> AlignAsync(IGitHubApi api, string repo, PagesPolicy policy, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(policy);
        var current = await InspectAsync(api, repo, policy, cancellationToken).ConfigureAwait(false);
        if (current.Error is { } error)
        {
            return new("error", [], error);
        }

        if (current.IsAligned)
        {
            return new("aligned", []);
        }

        var writes = policy.Enabled ? Writes(repo, policy, current.Enabled) : [];
        IReadOnlyList<string> planned = [.. writes.Select(Describe)];
        if (dryRun)
        {
            return new("dry_run", planned);
        }

        foreach (var write in writes)
        {
            var ran = await api.CallAsync(write, cancellationToken).ConfigureAwait(false);
            if (!ran.Ok)
            {
                return new("error", [], ran.Err.Trim());
            }
        }

        return new("success", planned);
    }

    /// <summary>The writes that bring an enabled policy's site into line: enable it, then configure it.</summary>
    internal static IReadOnlyList<ApiRequest> Writes(string repo, PagesPolicy policy, bool enabled)
    {
        var route = $"repos/{repo}/pages";
        var writes = new List<ApiRequest>();
        if (!enabled)
        {
            writes.Add(new("POST", route, PyRepr.Json(new JsonObject { ["build_type"] = policy.BuildType })));
        }

        // An existing site takes its build type on the update; a new one got it from the POST.
        var update = enabled ? new JsonObject { ["build_type"] = policy.BuildType } : new JsonObject();
        if (!string.IsNullOrEmpty(policy.Cname))
        {
            update["cname"] = policy.Cname;
            update["https_enforced"] = policy.HttpsEnforced;
        }
        else if (policy.HttpsEnforced)
        {
            // No `cname` key: GitHub reads `"cname": null` as removing the custom domain.
            update["https_enforced"] = true;
        }

        if (update.Count > 0)
        {
            writes.Add(new("PUT", route, PyRepr.Json(update)));
        }

        return writes;
    }

    /// <summary>A write as the dry run and failures name it.</summary>
    internal static string Describe(ApiRequest request) => $"{request.Method} {request.Route} {request.Body ?? string.Empty}".TrimEnd();
}

/// <summary>The GitHub Pages site, for repos whose policy enables it; a repo that declares nothing is left alone.</summary>
public sealed class PagesResource : Resource<PagesPolicy, LivePages?>
{
    /// <inheritdoc />
    public override string Name => "pages";

    /// <inheritdoc />
    public override PagesPolicy? Desired(RepoPolicies policies, Target target)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(target);
        return policies.Pages(target.Slug) is { Enabled: true } policy ? policy : null;
    }

    /// <inheritdoc />
    public override Task<LivePages?> ReadAsync(IGitHubApi api, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return PagesGovernance.ReadAsync(api, target.Slug, cancellationToken);
    }

    /// <inheritdoc />
    public override IReadOnlyList<Change> Diff(PagesPolicy desired, LivePages? observed) =>
        PagesGovernance.Drift(desired, observed) is { Count: > 0 } drift ? [new(Name, observed is null ? "enable" : "update", string.Join("; ", drift))] : [];

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ApplyAsync(IGitHubApi api, Target target, PagesPolicy desired, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(changes);
        if (!changes.Any(c => c.Kind == Change.Apply))
        {
            return [];
        }

        var enabled = changes.All(c => c.Action != "enable");
        foreach (var write in PagesGovernance.Writes(target.Slug, desired, enabled))
        {
            var ran = await api.CallAsync(write, cancellationToken).ConfigureAwait(false);
            if (!ran.Ok)
            {
                return [$"{PagesGovernance.Describe(write)}: {ran.Err.Trim()}"];
            }
        }

        return [];
    }
}
