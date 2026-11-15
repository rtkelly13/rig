namespace Rig.Core.Reconciliation;

/// <summary>
/// A desired-versus-observed reconciler: it reads external state into a typed plan, then walks that plan through the
/// same seam.
/// </summary>
/// <remarks>
/// <see cref="PlanAsync"/> must not write anything. <see cref="ApplyAsync"/> is idempotent: applying a plan twice
/// leaves the second plan empty. Errors are accumulated in the returned plan and the list of apply errors rather than
/// thrown, so a run can finish and report every problem it found.
/// </remarks>
/// <typeparam name="TTarget">What is reconciled: a clone, a worktree, a report, or a request record.</typeparam>
/// <typeparam name="TPlan">The plan built from <typeparamref name="TTarget"/>.</typeparam>
/// <typeparam name="TChange">The plan's change row type.</typeparam>
public interface IReconciler<in TTarget, TPlan, TChange>
    where TPlan : IPlan<TChange>
    where TChange : IChange
{
    /// <summary>Reads observed state and compares it with what is wanted. Must not write anything.</summary>
    /// <param name="target">What to reconcile.</param>
    /// <param name="ct">Stops the read.</param>
    Task<TPlan> PlanAsync(TTarget target, CancellationToken ct);

    /// <summary>Carries out every planned change through the reconciler's port, returning the errors it hit.</summary>
    /// <param name="plan">The plan <see cref="PlanAsync"/> produced.</param>
    /// <param name="ct">Stops between changes, never inside one.</param>
    Task<IReadOnlyList<string>> ApplyAsync(TPlan plan, CancellationToken ct);
}
