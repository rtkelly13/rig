namespace Rig.Core.Reconciliation;

/// <summary>
/// Convenience access to the plan contract from concrete plan types. Default interface members are only reachable
/// through an interface-typed reference, so a reconciler's plan exposes the same answers as extensions too.
/// </summary>
public static class PlanExtensions
{
    /// <summary>True when at least one row would change or did change observed state.</summary>
    /// <param name="plan">The plan to inspect.</param>
    /// <typeparam name="TChange">The plan's change row type.</typeparam>
    public static bool HasDrift<TChange>(this IPlan<TChange> plan)
        where TChange : IChange
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Changes.Any(change => change.Kind is ChangeKind.WouldChange or ChangeKind.Changed);
    }

    /// <summary>
    /// The process exit code the run should use: 2 on error, 1 on drift that was not applied, 0 otherwise.
    /// </summary>
    /// <param name="plan">The plan to inspect.</param>
    /// <param name="applied">Whether the plan was applied, rather than reported by a dry run.</param>
    /// <typeparam name="TChange">The plan's change row type.</typeparam>
    public static int ExitCode<TChange>(this IPlan<TChange> plan, bool applied)
        where TChange : IChange
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Errors.Count > 0 ? 2 : (plan.HasDrift() && !applied ? 1 : 0);
    }
}
