using Rig.Core.Processes;

namespace Rig.Core.Governance;

/// <summary>
/// <c>repo-governance plan</c>: every GitHub-side resource, desired → observed → plan → apply. Ported from
/// <c>rig.repo_governance.cli_plan</c>.
/// </summary>
public static class GovernancePlan
{
    /// <summary>
    /// Every resource the plan walks, in the order it applies them: settings before the ruleset that names the default
    /// branch, environments before anything that deploys.
    /// </summary>
    public static IReadOnlyList<IPlanner> Resources { get; } =
    [
        new RepoSettingsResource(),
        new RulesetResource(),
        new EnvironmentsResource(),
        new LabelsResource(),
        new PagesResource(),
    ];

    /// <summary>
    /// Plans every resource for <paramref name="target"/>; with <paramref name="apply"/>, makes the planned writes and
    /// records any that failed. A repo whose owner breaks the owner policy is refused outright: nothing is read or written.
    /// </summary>
    public static async Task<RepoPlan> PlanRepoAsync(
        RepoPolicies policies, IGitHubApi api, Target target, bool apply, IReadOnlyList<IPlanner>? planners = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var owner = RepoOwners.Inspect(policies, target.Slug);
        if (!owner.IsAligned)
        {
            return new(target.Slug, [new("owner", "refused", $"owner governance failure: {owner.Message}", Change.Refused)], []);
        }

        var planned = new List<Planned>();
        foreach (var planner in planners ?? Resources)
        {
            planned.Add(await planner.PlanAsync(policies, api, target, cancellationToken).ConfigureAwait(false));
        }

        var errors = planned.Where(p => p.Error is not null).Select(p => $"{p.Resource}: {p.Error}").ToList();
        if (apply)
        {
            foreach (var plan in planned)
            {
                errors.AddRange(await plan.Apply(cancellationToken).ConfigureAwait(false));
            }
        }

        return new(target.Slug, [.. planned.SelectMany(p => p.Changes)], errors);
    }

    /// <summary>
    /// 2 when GitHub could not be read or a write failed; once applied, 1 when a gate refused a write; else 1 when
    /// anything drifted; 0 when every repo matches.
    /// </summary>
    public static int ExitCode(IReadOnlyList<RepoPlan> plans, bool applied)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Any(p => p.Errors.Count > 0))
        {
            return 2;
        }

        if (applied)
        {
            return plans.Any(p => p.Changes.Any(c => c.Kind == Change.Refused)) ? 1 : 0;
        }

        return plans.Any(p => p.Drifted) ? 1 : 0;
    }
}
