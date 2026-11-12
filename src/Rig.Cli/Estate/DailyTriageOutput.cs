using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Estate;

namespace Rig.Cli.Estate;

/// <summary>How <c>rig estate daily-triage</c> prints its report: JSON for the orchestrator, a short list for a person.</summary>
internal static class DailyTriageOutput
{
    /// <summary>The report as the orchestrator reads it. The shape is documented in <c>docs/daily-orchestrator/policy.md</c>.</summary>
    public static JsonObject Json(DailyTriageReport report, string root, string role, IEnumerable<string> owners, string? author, DateTimeOffset now) => new()
    {
        ["root"] = root,
        ["role"] = role,
        ["author"] = author,
        ["generated_at"] = now.ToString("o", CultureInfo.InvariantCulture),
        ["owners"] = new JsonArray([.. owners.Select(owner => (JsonNode)owner)]),
        ["repos"] = new JsonArray([.. report.Repos.Select(repo => (JsonNode)new JsonObject
        {
            ["repo"] = repo.Repo,
            ["path"] = repo.Path,
            ["trusted"] = repo.Trusted,
            ["open_prs"] = repo.OpenPulls,
            ["error"] = repo.Error,
        })]),
        ["report_only"] = new JsonArray([.. report.ReportOnly.Select(clone => (JsonNode)new JsonObject
        {
            ["repo"] = clone.Repo,
            ["path"] = clone.Path,
        })]),
        ["pulls"] = new JsonArray([.. report.Pulls.Select(pull => (JsonNode)new JsonObject
        {
            ["id"] = pull.Id,
            ["repo"] = pull.Repo,
            ["path"] = pull.Path,
            ["trusted"] = pull.Trusted,
            ["number"] = pull.Number,
            ["title"] = pull.Title,
            ["url"] = pull.Link,
            ["author"] = pull.Author,
            ["head"] = pull.Head,
            ["base"] = pull.Base,
            ["updated_at"] = pull.UpdatedAt?.ToString("o", CultureInfo.InvariantCulture),
            ["lane"] = pull.Lane.ToString().ToLowerInvariant(),
            ["signals"] = Strings(pull.Signals),
            ["failing_checks"] = Strings(pull.FailingChecks),
            ["pending_checks"] = Strings(pull.PendingChecks),
            ["unresolved_threads"] = pull.UnresolvedThreads,
            ["superseded_by"] = pull.SupersededBy,
        })]),
    };

    /// <summary>The report for a person: open PRs grouped by lane, then any repo GitHub could not be read for.</summary>
    public static string Text(DailyTriageReport report)
    {
        var text = new StringBuilder();
        _ = text.Append(CultureInfo.InvariantCulture, $"{report.Pulls.Count} open PRs across {report.Repos.Count} owned repos ({report.ReportOnly.Count} clones report-only)");
        foreach (var lane in new[] { TriageLane.Work, TriageLane.Owner, TriageLane.Wait })
        {
            var pulls = report.Pulls.Where(pull => pull.Lane == lane).ToList();
            if (pulls.Count == 0)
            {
                continue;
            }

            _ = text.Append(CultureInfo.InvariantCulture, $"\n\n{lane.ToString().ToLowerInvariant()} ({pulls.Count})");
            foreach (var pull in pulls)
            {
                _ = text.Append(CultureInfo.InvariantCulture, $"\n  {pull.Repo}#{pull.Number}  {string.Join(", ", pull.Signals)}  {pull.Link}");
            }
        }

        foreach (var repo in report.Repos.Where(repo => repo.Error is not null))
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"\n\n! {repo.Repo}: {repo.Error}");
        }

        return text.ToString();
    }

    private static JsonArray Strings(IEnumerable<string> values) => new([.. values.Select(value => (JsonNode)value)]);
}
