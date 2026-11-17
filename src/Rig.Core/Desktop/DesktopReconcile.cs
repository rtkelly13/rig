using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>A virtual desktop identified by its 1-based position and name.</summary>
/// <param name="Index">Its position, from 1.</param>
/// <param name="Name">Its name.</param>
public sealed record DesktopItem(int Index, string Name);

/// <summary>A missing virtual desktop the reconciliation creates.</summary>
/// <param name="Index">The position it takes, from 1.</param>
/// <param name="Name">The name it is given.</param>
/// <param name="Created">Whether it was created; false only in a plan.</param>
public sealed record DesktopCreationAction(int Index, string Name, bool Created = true);

/// <summary>A virtual desktop whose live name differs from the plan's.</summary>
/// <param name="Index">Its position, from 1.</param>
/// <param name="CurrentName">The live name, empty when Windows has none.</param>
/// <param name="TargetName">The name the plan declares.</param>
/// <param name="Success">Whether the rename took.</param>
/// <param name="Error">Why it failed, when it did.</param>
public sealed record DesktopRenameAction(int Index, string CurrentName, string TargetName, bool Success = true, string? Error = null);

/// <summary>
/// The drift between the declared virtual desktops and the live ones: what to retire, create, rename, leave unchanged
/// and report as an unmanaged surplus. Ported from <c>desktop_manager.desktop_reconcile.DesktopReconciliationPlan</c>.
/// </summary>
/// <param name="TargetDesktops">The names the plan declares, in order.</param>
/// <param name="CurrentDesktops">The names that were live when the plan was drawn.</param>
public sealed record DesktopReconciliationPlan(IReadOnlyList<string> TargetDesktops, IReadOnlyList<string> CurrentDesktops)
{
    /// <summary>Switched-off desktops to remove.</summary>
    public IReadOnlyList<string> ToRetire { get; init; } = [];

    /// <summary>Missing desktops to add.</summary>
    public IReadOnlyList<DesktopCreationAction> ToCreate { get; init; } = [];

    /// <summary>Desktops whose name has to change.</summary>
    public IReadOnlyList<DesktopRenameAction> ToRename { get; init; } = [];

    /// <summary>Desktops that already match.</summary>
    public IReadOnlyList<DesktopItem> Unchanged { get; init; } = [];

    /// <summary>Surplus desktops past the declared count.</summary>
    public IReadOnlyList<DesktopItem> Unmanaged { get; init; } = [];

    /// <summary>Whether anything has to change.</summary>
    public bool HasDrift => ToRetire.Count > 0 || ToCreate.Count > 0 || ToRename.Count > 0;
}

/// <summary>
/// What reconciling the live desktops did, or (in a dry run) what it would do. Ported from
/// <c>desktop_manager.desktop_reconcile.DesktopReconciliationResult</c>.
/// </summary>
/// <param name="Plan">The drift the result applied.</param>
public sealed record DesktopReconciliationResult(DesktopReconciliationPlan Plan)
{
    /// <summary>The switched-off desktops removed, in the order they went.</summary>
    public IReadOnlyList<string> Retired { get; init; } = [];

    /// <summary>The desktops added.</summary>
    public IReadOnlyList<DesktopCreationAction> Created { get; init; } = [];

    /// <summary>The renames attempted.</summary>
    public IReadOnlyList<DesktopRenameAction> Renamed { get; init; } = [];

    /// <summary>The renames that failed.</summary>
    public IReadOnlyList<DesktopRenameAction> FailedRenames { get; init; } = [];

    /// <summary>The desktops that already matched.</summary>
    public IReadOnlyList<DesktopItem> Unchanged { get; init; } = [];

    /// <summary>The surplus desktops past the declared count.</summary>
    public IReadOnlyList<DesktopItem> Unmanaged { get; init; } = [];

    /// <summary>Whether nothing was changed.</summary>
    public bool DryRun { get; init; }

    /// <summary>Whether every rename took.</summary>
    public bool Converged => FailedRenames.Count == 0;
}

/// <summary>
/// Virtual desktop reconciliation (Infrastructure as Code): the pure drift planner and the orchestration that applies
/// it through <see cref="IVirtualDesktops"/>. Ported from <c>desktop_manager.desktop_reconcile</c>.
/// </summary>
public static class DesktopReconcile
{
    /// <summary>
    /// The drift between <paramref name="targetDesktops"/> and <paramref name="currentNames"/>. Names in
    /// <paramref name="retiredNames"/> are removed first; a surplus desktop is reported but never removed here.
    /// </summary>
    public static DesktopReconciliationPlan Plan(
        IReadOnlyList<string> targetDesktops,
        IReadOnlyList<string> retiredNames,
        IReadOnlyList<string> currentNames)
    {
        ArgumentNullException.ThrowIfNull(targetDesktops);
        ArgumentNullException.ThrowIfNull(retiredNames);
        ArgumentNullException.ThrowIfNull(currentNames);

        var targets = targetDesktops.ToList();
        var retired = retiredNames.Where(name => name.Length > 0).Select(UnicodeText.CaseFold).ToHashSet(StringComparer.Ordinal);
        var current = currentNames.ToList();

        var toRetire = current.Where(name => name.Length > 0 && retired.Contains(UnicodeText.CaseFold(name))).ToList();

        // A run that would clear every desktop keeps the first one: Windows cannot remove its last desktop.
        var postRetire = current.Where(name => name.Length == 0 || !retired.Contains(UnicodeText.CaseFold(name))).ToList();
        if (current.Count > 0 && postRetire.Count == 0)
        {
            postRetire = [current[0]];
        }

        var targetCount = targets.Count;
        var effectiveCount = postRetire.Count;

        var toCreate = new List<DesktopCreationAction>();
        if (effectiveCount < targetCount)
        {
            for (var index = effectiveCount + 1; index <= targetCount; index++)
            {
                toCreate.Add(new(index, targets[index - 1]));
            }
        }

        var toRename = new List<DesktopRenameAction>();
        var unchanged = new List<DesktopItem>();
        for (var index = 1; index <= targets.Count; index++)
        {
            var targetName = targets[index - 1];
            if (index <= effectiveCount)
            {
                var currentName = postRetire[index - 1];
                if (currentName == targetName)
                {
                    unchanged.Add(new(index, targetName));
                }
                else
                {
                    toRename.Add(new(index, currentName, targetName));
                }
            }
            else
            {
                toRename.Add(new(index, string.Empty, targetName));
            }
        }

        var unmanaged = new List<DesktopItem>();
        if (effectiveCount > targetCount)
        {
            for (var index = targetCount + 1; index <= effectiveCount; index++)
            {
                unmanaged.Add(new(index, postRetire[index - 1]));
            }
        }

        return new(targets, current)
        {
            ToRetire = toRetire,
            ToCreate = toCreate,
            ToRename = toRename,
            Unchanged = unchanged,
            Unmanaged = unmanaged,
        };
    }

    /// <summary>
    /// Bring the live desktops to the declared state: retire switched-off ones, add the missing ones, then rename each
    /// to its declared name. <paramref name="dryRun"/> draws the plan and changes nothing.
    /// </summary>
    public static DesktopReconciliationResult Reconcile(
        IReadOnlyList<string> targetDesktops,
        IVirtualDesktops desktops,
        IReadOnlyList<string> retiredNames,
        TimeSpan switchDelay,
        bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(targetDesktops);
        ArgumentNullException.ThrowIfNull(desktops);
        ArgumentNullException.ThrowIfNull(retiredNames);

        var targets = targetDesktops.ToList();
        var plan = Plan(targets, retiredNames, desktops.Names());
        if (dryRun)
        {
            return new(plan) { Unchanged = plan.Unchanged, Unmanaged = plan.Unmanaged, DryRun = true };
        }

        var retired = retiredNames.Count > 0
            ? VirtualDesktopOperations.RetireSwitchedOff(desktops, targets.Count, retiredNames)
            : [];

        var beforeCount = desktops.Count;
        _ = VirtualDesktopOperations.EnsureCount(desktops, targets.Count);
        var afterCount = desktops.Count;

        var created = new List<DesktopCreationAction>();
        if (afterCount > beforeCount)
        {
            for (var index = beforeCount + 1; index <= Math.Min(targets.Count, afterCount); index++)
            {
                created.Add(new(index, targets[index - 1]));
            }
        }

        var renamed = new List<DesktopRenameAction>();
        var failed = new List<DesktopRenameAction>();
        var unchanged = new List<DesktopItem>();

        var namesAfter = desktops.Names();
        for (var index = 1; index <= targets.Count; index++)
        {
            var targetName = targets[index - 1];
            var currentName = index <= namesAfter.Count ? namesAfter[index - 1] : string.Empty;
            if (currentName == targetName)
            {
                unchanged.Add(new(index, targetName));
                continue;
            }

            var ok = VirtualDesktopOperations.Rename(desktops, index, targetName);
            var action = new DesktopRenameAction(index, currentName, targetName, ok, ok ? null : "Rename failed");
            renamed.Add(action);
            if (!ok)
            {
                failed.Add(action);
            }
        }

        var unmanaged = new List<DesktopItem>();
        if (namesAfter.Count > targets.Count)
        {
            for (var index = targets.Count + 1; index <= namesAfter.Count; index++)
            {
                unmanaged.Add(new(index, namesAfter[index - 1]));
            }
        }

        return new(plan)
        {
            Retired = retired,
            Created = created,
            Renamed = renamed,
            FailedRenames = failed,
            Unchanged = unchanged,
            Unmanaged = unmanaged,
        };
    }
}
