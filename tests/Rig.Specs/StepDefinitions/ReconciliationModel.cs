using Rig.Core.Reconciliation;

namespace Rig.Specs.StepDefinitions;

/// <summary>One resource the reconciliation pipeline would change.</summary>
/// <param name="Subject">The resource path.</param>
/// <param name="Detail">Why it changes.</param>
/// <param name="Kind">What reconciliation decided.</param>
public sealed record ResourceChange(string Subject, string Detail, ChangeKind Kind) : IChange;

/// <summary>A plan of <see cref="ResourceChange"/> entries, ordered by subject.</summary>
public sealed class ResourcePlan(IReadOnlyList<ResourceChange> changes) : IPlan<ResourceChange>
{
    /// <inheritdoc />
    public IReadOnlyList<ResourceChange> Changes { get; } = changes;

    /// <inheritdoc />
    public IReadOnlyList<string> Errors => [];
}

/// <summary>The desired and observed file sets a scenario compares.</summary>
/// <param name="Desired">What should exist.</param>
/// <param name="Observed">What exists now.</param>
public sealed record ResourceTarget(
    IReadOnlyDictionary<string, string> Desired,
    IReadOnlyDictionary<string, string> Observed);

/// <summary>
/// Compares a desired set of files with what exists. This stands in for a real reconciler: it is pure, it names every
/// change, and it keeps <see cref="ChangeKind.Aligned"/> rows so a clean estate is a plan with no mutations rather than
/// an absent plan.
/// </summary>
/// <param name="apply">Receives each would-change row when the plan is applied; null applies nothing.</param>
public sealed class ResourceReconciler(Action<ResourceChange>? apply = null)
    : IReconciler<ResourceTarget, ResourcePlan, ResourceChange>
{
    /// <summary>Reads observed state and builds the plan. Never writes.</summary>
    /// <param name="target">What should exist and what exists.</param>
    /// <param name="ct">Stops before planning.</param>
    public Task<ResourcePlan> PlanAsync(ResourceTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Plan(target.Desired, target.Observed));
    }

    /// <summary>Walks the plan's would-change rows through the fixture's sink.</summary>
    /// <param name="plan">The plan <see cref="PlanAsync"/> produced.</param>
    /// <param name="ct">Stops between changes.</param>
    public Task<IReadOnlyList<string>> ApplyAsync(ResourcePlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (apply is null)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        foreach (var change in plan.Changes.Where(change => change.Kind == ChangeKind.WouldChange))
        {
            ct.ThrowIfCancellationRequested();
            apply(change);
        }

        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Compares desired with observed into typed rows.</summary>
    /// <param name="desired">What should exist.</param>
    /// <param name="observed">What exists now.</param>
    public static ResourcePlan Plan(IReadOnlyDictionary<string, string> desired, IReadOnlyDictionary<string, string> observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);
        var changes = new List<ResourceChange>();
        foreach (var target in desired.Keys.Union(observed.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var wanted = desired.TryGetValue(target, out var desiredContent);
            var exists = observed.TryGetValue(target, out var observedContent);
            changes.Add((wanted, exists) switch
            {
                (true, false) => new ResourceChange(target, "does not exist yet", ChangeKind.WouldChange),
                (false, true) => new ResourceChange(target, "exists unexpectedly", ChangeKind.WouldChange),
                (true, true) when string.Equals(desiredContent, observedContent, StringComparison.Ordinal) => new ResourceChange(target, "already in the desired state", ChangeKind.Aligned),
                (true, true) => new ResourceChange(target, "differs from the desired state", ChangeKind.WouldChange),
                _ => throw new InvalidOperationException($"no reconciliation rule for {target}"),
            });
        }

        return new ResourcePlan(changes);
    }
}
