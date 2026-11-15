namespace Rig.Core.Reconciliation;

/// <summary>What one run of a plan did, or would have done.</summary>
/// <param name="Changes">The mutating changes in the plan, in plan order; <see cref="ChangeKind.NoOp"/> entries are not a mutation.</param>
/// <param name="Applied">How many changes reached the sink; always zero for a dry run.</param>
/// <param name="DryRun">Whether the run was a dry run.</param>
public sealed record ReconciliationReport(IReadOnlyList<IChange> Changes, int Applied, bool DryRun)
{
    /// <summary>How many mutating changes the plan holds.</summary>
    public int Drift => Changes.Count;

    /// <summary>True when nothing reached the sink.</summary>
    public bool MutatedNothing => Applied == 0;
}

/// <summary>
/// Applies a plan through the caller's sink, or only reports it for a dry run. The plan is pure, so the only way a dry
/// run can change anything is by calling the sink: it never does. Every <c>--dry-run</c> flag in the estate makes
/// exactly this promise.
/// </summary>
public static class ReconciliationRunner
{
    /// <summary>Runs <paramref name="plan"/>, calling <paramref name="apply"/> once per mutating change unless dry.</summary>
    /// <param name="plan">The desired-versus-observed comparison to carry out.</param>
    /// <param name="dryRun">Report only; never call <paramref name="apply"/>.</param>
    /// <param name="apply">Receives each mutating change, in plan order.</param>
    public static ReconciliationReport Run(IPlan<IChange> plan, bool dryRun, Action<IChange> apply)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(apply);
        var changes = plan.Changes.Where(change => change.Kind != ChangeKind.NoOp).ToList();
        if (!dryRun)
        {
            foreach (var change in changes)
            {
                apply(change);
            }
        }

        return new ReconciliationReport(changes, dryRun ? 0 : changes.Count, dryRun);
    }
}
