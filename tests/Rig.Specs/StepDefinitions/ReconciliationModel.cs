using Rig.Core.Reconciliation;

namespace Rig.Specs.StepDefinitions;

/// <summary>One resource the reconciliation pipeline would create, update or delete.</summary>
/// <param name="Target">The resource path.</param>
/// <param name="Description">Why it changes.</param>
/// <param name="Kind">Which mutation it is.</param>
public sealed record ResourceChange(string Target, string Description, ChangeKind Kind) : IChange;

/// <summary>A plan of <see cref="ResourceChange"/> entries, ordered by target.</summary>
public sealed class ResourcePlan(IReadOnlyList<ResourceChange> changes) : IPlan<ResourceChange>
{
    /// <inheritdoc />
    public IReadOnlyList<ResourceChange> Changes { get; } = changes;
}

/// <summary>
/// Compares a desired set of files with what exists. This stands in for a real reconciler: it is pure, it names every
/// change, and it keeps <see cref="ChangeKind.NoOp"/> rows so a clean estate is a plan with no mutations rather than
/// an absent plan.
/// </summary>
public sealed class ResourceReconciler : IReconciler<IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, string>, ResourceChange>
{
    /// <inheritdoc />
    public IPlan<ResourceChange> Plan(IReadOnlyDictionary<string, string> desired, IReadOnlyDictionary<string, string> observed)
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
                (true, false) => new ResourceChange(target, "does not exist yet", ChangeKind.Create),
                (false, true) => new ResourceChange(target, "exists unexpectedly", ChangeKind.Delete),
                (true, true) when string.Equals(desiredContent, observedContent, StringComparison.Ordinal) => new ResourceChange(target, "already in the desired state", ChangeKind.NoOp),
                (true, true) => new ResourceChange(target, "differs from the desired state", ChangeKind.Update),
                _ => throw new InvalidOperationException($"no reconciliation rule for {target}"),
            });
        }

        return new ResourcePlan(changes);
    }
}
