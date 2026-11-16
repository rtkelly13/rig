using System.Diagnostics;
using Rig.Core.GitHub;

namespace Rig.Core.Chrome;

/// <summary>Which close rules a run applies.</summary>
/// <param name="GitHub">Run the PR rules, asking GitHub.</param>
/// <param name="Sites">Close duplicate non-PR pages by per-site title rules.</param>
/// <param name="MaxMemoryGb">The memory budget, if any, applied after the other rules.</param>
public sealed record ClosePlanOptions(bool GitHub = true, bool Sites = true, double? MaxMemoryGb = null);

/// <summary>
/// The close plans <c>prune</c> and <c>organise</c> share: the PR rules, the site rules and the memory budget, each
/// printed as it is made. Ported from <c>_close_plans</c> and <c>_make_close_plans</c> in
/// <c>desktop_manager.chrome.cli</c>.
/// </summary>
public static class ChromeClosePlans
{
    private static readonly ActivitySource Source = new("Rig.Chrome");

    /// <summary>
    /// Every plan for <paramref name="windows"/>, in the order they run: PRs, then site duplicates, then the memory
    /// budget. Each plan's lines, and any repository GitHub could not answer for, go to <paramref name="say"/> as the
    /// plan is made. One <c>chrome_tabs.plan_closes</c> span records how many tabs each plan closes.
    /// </summary>
    /// <param name="windows">The windows to plan over.</param>
    /// <param name="resolution">GitHub's answer for the PRs in them.</param>
    /// <param name="options">Which rules to apply.</param>
    /// <param name="say">Writes a line for the user.</param>
    public static IReadOnlyList<IClosePlan> Plan(
        IReadOnlyList<ChromeWindow> windows,
        Resolution resolution,
        ClosePlanOptions options,
        Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(say);
        using var activity = Source.StartActivity("chrome_tabs.plan_closes");
        var plans = new List<IClosePlan>();
        var held = new HashSet<RuntimeId>();
        if (options.GitHub)
        {
            var prPlan = PrPrune.Plan(windows, resolution);
            plans.Add(prPlan);
            held.UnionWith(prPlan.Reports.Select(report => report.Item.Tab.RuntimeId));
            foreach (var (slug, error) in resolution.FailedRepos.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                say($"{slug}: open PRs unreadable, chain rules skipped: {error}");
            }

            SayAll(say, TabPlanText.Prune(prPlan));
        }

        if (options.Sites)
        {
            var sitePlan = Dedupe.Plan(windows);
            plans.Add(sitePlan);
            held.UnionWith(sitePlan.Reports.Select(report => report.Item.Tab.RuntimeId));
            SayAll(say, TabPlanText.Dedupe(sitePlan));
        }

        if (options.MaxMemoryGb is { } budget)
        {
            var closing = plans.SelectMany(plan => plan.Closes).Select(close => close.Item.Tab.RuntimeId).ToHashSet();
            var memoryPlan = TabRank.PlanMemory(TabRank.Rank(windows, resolution), budget * 1024, closing, held);
            plans.Add(memoryPlan);
            SayAll(say, TabPlanText.Memory(memoryPlan));
        }

        foreach (var plan in plans)
        {
            activity?.SetTag($"close.{Kind(plan)}", plan.Closes.Count);
        }

        activity?.SetTag("close.planned", plans.Sum(plan => plan.Closes.Count));
        return plans;
    }

    private static string Kind(IClosePlan plan) => plan switch
    {
        PrunePlan => "pr",
        DedupePlan => "sites",
        MemoryPlan => "memory",
        _ => "other",
    };

    private static void SayAll(Action<string> say, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            say(line);
        }
    }
}
