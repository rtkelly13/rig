namespace Rig.Core.Reconciliation;

/// <summary>Root marker for every reconciliation plan, whatever change type it carries.</summary>
public interface IPlan
{
    /// <summary>True when at least one row would change or did change observed state.</summary>
    bool HasDrift { get; }
}

/// <summary>
/// The result of comparing desired state with observed state: typed changes plus any errors raised while planning.
/// </summary>
/// <remarks>
/// A plan is pure data. Applying it goes through the same reconciler that produced it, so a dry run can never write
/// anything by constructing a plan.
/// </remarks>
/// <typeparam name="TChange">The reconciler's change row type.</typeparam>
public interface IPlan<out TChange> : IPlan
    where TChange : IChange
{
    /// <summary>Every typed change in evaluation order.</summary>
    IReadOnlyList<TChange> Changes { get; }

    /// <summary>Errors raised while reading observed state or building the plan.</summary>
    IReadOnlyList<string> Errors { get; }

    /// <summary>True when at least one row would change or did change observed state.</summary>
    bool IPlan.HasDrift => Changes.Any(change => change.Kind is ChangeKind.WouldChange or ChangeKind.Changed);

    /// <summary>True when at least one row would change or did change observed state.</summary>
    new bool HasDrift => Changes.Any(change => change.Kind is ChangeKind.WouldChange or ChangeKind.Changed);

    /// <summary>
    /// The process exit code the run should use: 2 on error, 1 on drift that was not applied, 0 otherwise.
    /// </summary>
    /// <param name="applied">Whether the plan was applied, rather than reported by a dry run.</param>
    int ExitCode(bool applied) => Errors.Count > 0 ? 2 : (HasDrift && !applied ? 1 : 0);
}
