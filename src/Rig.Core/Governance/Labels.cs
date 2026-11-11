using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>A label as it exists on a repository, normalised for comparison against the taxonomy.</summary>
/// <param name="Color">Lowercased hex, without <c>#</c>.</param>
/// <param name="Description">Its description, empty when it has none.</param>
public sealed record RepoLabel(string Color, string Description);

/// <summary>What it takes to bring one taxonomy label into line.</summary>
/// <param name="LabelName">The label.</param>
/// <param name="Action"><c>create</c> or <c>update</c>.</param>
/// <param name="Details">Why.</param>
/// <param name="Color">The taxonomy's colour.</param>
/// <param name="Description">The taxonomy's description.</param>
public sealed record LabelDifference(string LabelName, string Action, string Details, string Color, string Description);

/// <summary>A repo's labels against its taxonomy.</summary>
/// <param name="TotalStandard">How many labels the taxonomy declares.</param>
/// <param name="Missing">Taxonomy labels the repo lacks.</param>
/// <param name="Mismatched">Taxonomy labels the repo holds differently.</param>
/// <param name="Differences">What it takes to align each.</param>
public sealed record LabelAudit(int TotalStandard, IReadOnlyList<string> Missing, IReadOnlyList<string> Mismatched, IReadOnlyList<LabelDifference> Differences)
{
    /// <summary>Whether nothing is missing or mismatched.</summary>
    public bool IsFullyAligned => Missing.Count == 0 && Mismatched.Count == 0;
}

/// <summary>What a label sync did: whether anything differed, and a line per label.</summary>
/// <param name="Changed">Whether anything differed.</param>
/// <param name="Messages">A line per label acted on.</param>
public sealed record SyncOutcome(bool Changed, IReadOnlyList<string> Messages);

/// <summary>
/// GitHub label governance: holds repos to the <c>labels</c> taxonomy in <c>config/repo_policies.json</c>. Labels outside
/// the taxonomy are left alone; a missing taxonomy label is created, and one whose colour or description differs is
/// updated. Ported from <c>rig.repo_governance.label_taxonomy</c>.
/// </summary>
public static class LabelTaxonomy
{
    // GitHub's largest page for the labels endpoint.
    private const int PageSize = 100;

    /// <summary>
    /// Every label on <paramref name="repo"/>, read page by page until a short one, so a governed label on a later page
    /// does not read as missing; throws <see cref="GitHubReadException"/> when they cannot be read.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, RepoLabel>> ReadAsync(IGitHubApi api, string repo, CancellationToken cancellationToken = default)
    {
        var route = $"repos/{repo}/labels?per_page={PageSize}";
        var labels = new Dictionary<string, RepoLabel>(StringComparer.Ordinal);
        var page = Page(await GitHubCalls.ReadJsonAsync(api, route, cancellationToken).ConfigureAwait(false), route);
        var number = 1;
        Collect(page);
        while (page.Count == PageSize)
        {
            number++;
            var next = $"{route}&page={number}";
            page = Page(await GitHubCalls.ReadJsonAsync(api, next, cancellationToken).ConfigureAwait(false), next);
            Collect(page);
        }

        return labels;

        void Collect(JsonArray entries)
        {
            foreach (var entry in entries)
            {
                var name = GitHubCalls.Str(entry, "name")!;
                labels[name] = new((GitHubCalls.Str(entry, "color") ?? string.Empty).ToLowerInvariant(), GitHubCalls.Str(entry, "description") ?? string.Empty);
            }
        }
    }

    /// <summary>What it takes to bring <paramref name="labels"/> up to <paramref name="taxonomy"/>.</summary>
    public static IReadOnlyList<LabelDifference> Differences(IReadOnlyList<LabelSpec> taxonomy, IReadOnlyDictionary<string, RepoLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(taxonomy);
        ArgumentNullException.ThrowIfNull(labels);
        var differences = new List<LabelDifference>();
        foreach (var std in taxonomy)
        {
            if (!labels.TryGetValue(std.Name, out var current))
            {
                differences.Add(new(std.Name, "create", "Label missing from repository", std.Color, std.Description));
                continue;
            }

            var reasons = new List<string>();
            var (have, want) = (current.Color.ToLowerInvariant(), std.Color.ToLowerInvariant());
            if (have != want)
            {
                reasons.Add($"color mismatch (#{have} vs #{want})");
            }

            if (current.Description != std.Description)
            {
                reasons.Add("description mismatch");
            }

            if (reasons.Count > 0)
            {
                differences.Add(new(std.Name, "update", string.Join(", ", reasons), std.Color, std.Description));
            }
        }

        return differences;
    }

    /// <summary>Audits <paramref name="labels"/> against <paramref name="taxonomy"/>.</summary>
    public static LabelAudit Inspect(IReadOnlyList<LabelSpec> taxonomy, IReadOnlyDictionary<string, RepoLabel> labels)
    {
        var differences = Differences(taxonomy, labels);
        return new(
            taxonomy.Count,
            [.. differences.Where(d => d.Action == "create").Select(d => d.LabelName)],
            [.. differences.Where(d => d.Action == "update").Select(d => d.LabelName)],
            differences);
    }

    /// <summary>Brings <paramref name="repo"/>'s labels up to <paramref name="taxonomy"/>, or with <paramref name="dryRun"/> says what it would do. Throws <see cref="GitHubReadException"/> when the labels cannot be read.</summary>
    public static async Task<SyncOutcome> SyncAsync(IGitHubApi api, string repo, IReadOnlyList<LabelSpec> taxonomy, bool dryRun, CancellationToken cancellationToken = default)
    {
        var audit = Inspect(taxonomy, await ReadAsync(api, repo, cancellationToken).ConfigureAwait(false));
        if (audit.IsFullyAligned)
        {
            return new(false, ["All standard labels already aligned."]);
        }

        var messages = new List<string>();
        foreach (var diff in audit.Differences)
        {
            if (dryRun)
            {
                messages.Add(diff.Action == "create"
                    ? $"[Dry Run] Would create label '{diff.LabelName}' (#{diff.Color})"
                    : $"[Dry Run] Would update label '{diff.LabelName}' ({diff.Details})");
                continue;
            }

            messages.Add(await WriteAsync(api, repo, diff, cancellationToken).ConfigureAwait(false) switch
            {
                { } error => $"Failed to {diff.Action} '{diff.LabelName}': {error}",
                null when diff.Action == "create" => $"Created label '{diff.LabelName}' (#{diff.Color})",
                null => $"Updated label '{diff.LabelName}'",
            });
        }

        return new(true, messages);
    }

    /// <summary>Creates or updates one label; returns why it failed, or null.</summary>
    internal static async Task<string?> WriteAsync(IGitHubApi api, string repo, LabelDifference diff, CancellationToken cancellationToken)
    {
        var ran = diff.Action == "create"
            ? await GitHubCalls.SendAsync(
                api,
                "POST",
                $"repos/{repo}/labels",
                PyRepr.Json(new JsonObject { ["name"] = diff.LabelName, ["color"] = diff.Color, ["description"] = diff.Description }),
                cancellationToken).ConfigureAwait(false)
            : await GitHubCalls.SendAsync(
                api,
                "PATCH",
                $"repos/{repo}/labels/{Uri.EscapeDataString(diff.LabelName)}",
                PyRepr.Json(new JsonObject { ["color"] = diff.Color, ["description"] = diff.Description }),
                cancellationToken).ConfigureAwait(false);
        return ran.Ok ? null : ran.Err.Trim();
    }

    // A labels page: an array of objects each carrying a string name.
    private static JsonArray Page(JsonNode node, string route) =>
        node is JsonArray entries && entries.All(e => GitHubCalls.Str(e, "name") is not null)
            ? entries
            : throw new GitHubReadException($"GET {route}: unreadable response");
}

/// <summary>The <c>labels</c> taxonomy; a repo whose policy declares none is left alone.</summary>
public sealed class LabelsResource : Resource<IReadOnlyList<LabelSpec>, IReadOnlyDictionary<string, RepoLabel>>
{
    /// <inheritdoc />
    public override string Name => "labels";

    /// <inheritdoc />
    public override IReadOnlyList<LabelSpec>? Desired(RepoPolicies policies, Target target)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(target);
        return policies.Labels(target.Slug) is { Count: > 0 } labels ? labels : null;
    }

    /// <inheritdoc />
    public override Task<IReadOnlyDictionary<string, RepoLabel>> ReadAsync(IGitHubApi api, Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return LabelTaxonomy.ReadAsync(api, target.Slug, cancellationToken);
    }

    /// <inheritdoc />
    public override IReadOnlyList<Change> Diff(IReadOnlyList<LabelSpec> desired, IReadOnlyDictionary<string, RepoLabel> observed) =>
        [.. LabelTaxonomy.Differences(desired, observed).Select(d => new Change(Name, d.Action, d.Details, Subject: d.LabelName))];

    /// <inheritdoc />
    public override async Task<IReadOnlyList<string>> ApplyAsync(IGitHubApi api, Target target, IReadOnlyList<LabelSpec> desired, IReadOnlyList<Change> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(changes);
        var wanted = desired.ToDictionary(label => label.Name, StringComparer.Ordinal);
        var failures = new List<string>();
        foreach (var change in changes)
        {
            var label = wanted[change.Subject];
            var diff = new LabelDifference(label.Name, change.Action, change.Detail, label.Color, label.Description);
            if (await LabelTaxonomy.WriteAsync(api, target.Slug, diff, cancellationToken).ConfigureAwait(false) is { } error)
            {
                failures.Add($"{change.Action} label '{label.Name}' on {target.Slug}: {error}");
            }
        }

        return failures;
    }
}
