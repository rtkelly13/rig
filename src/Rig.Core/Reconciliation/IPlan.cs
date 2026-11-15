namespace Rig.Core.Reconciliation;

/// <summary>
/// Root contract for all reconciliation plans in Rig.
/// </summary>
public interface IPlan
{
    /// <summary>True when there is zero drift between desired and observed state.</summary>
    bool IsClean { get; }

    /// <summary>Count of mutating changes in the plan.</summary>
    int DriftCount { get; }
}

/// <summary>
/// A typed reconciliation plan containing planned atomic changes.
/// </summary>
/// <typeparam name="TChange">The specific change type implementing <see cref="IChange"/>.</typeparam>
public interface IPlan<out TChange> : IPlan
    where TChange : IChange
{
    /// <summary>All planned changes in evaluation order.</summary>
    IReadOnlyList<TChange> Changes { get; }

    /// <summary>True when there is zero drift between desired and observed state.</summary>
    new bool IsClean => Changes.All(c => c.Kind == ChangeKind.NoOp);

    /// <summary>Count of mutating changes in the plan.</summary>
    new int DriftCount => Changes.Count(c => c.Kind != ChangeKind.NoOp);

    bool IPlan.IsClean => IsClean;

    int IPlan.DriftCount => DriftCount;
}

/// <summary>
/// Reconciler producing a typed plan by comparing desired state with observed state.
/// Pure and free of side effects.
/// </summary>
/// <typeparam name="TDesired">Desired state model.</typeparam>
/// <typeparam name="TObserved">Observed state model.</typeparam>
/// <typeparam name="TChange">Change item type.</typeparam>
public interface IReconciler<in TDesired, in TObserved, out TChange>
    where TChange : IChange
{
    /// <summary>Compare desired state with observed state and produce a plan.</summary>
    IPlan<TChange> Plan(TDesired desired, TObserved observed);
}
