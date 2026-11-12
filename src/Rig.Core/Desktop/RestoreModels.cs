using System.Globalization;

namespace Rig.Core.Desktop;

/// <summary>What a restore does with one entry.</summary>
public enum EntryAction
{
    /// <summary>Start it.</summary>
    Launch,

    /// <summary>Move an open window into its slot.</summary>
    Reuse,

    /// <summary>Its window is already in its slot; leave it alone.</summary>
    Satisfied,
}

/// <summary>One entry's step in a restore. Ported from <c>desktop_manager.restore_models.PlannedEntryExecution</c>.</summary>
/// <param name="Desktop">The desktop it belongs to.</param>
/// <param name="DesktopIndex">That desktop's number, from 1.</param>
/// <param name="Entry">The entry.</param>
/// <param name="Description">What launching it does, for the dry-run listing.</param>
/// <param name="Action">What the restore does with it.</param>
/// <param name="ReusableWindow">The window it reuses, when <paramref name="Action"/> is <see cref="EntryAction.Reuse"/>.</param>
public sealed record PlannedEntryExecution(
    DesktopPlan Desktop,
    int DesktopIndex,
    LaunchEntry Entry,
    string Description,
    EntryAction Action,
    LiveWindow? ReusableWindow = null);

/// <summary>
/// One desktop's steps in a restore. Mutable: a retry replans its steps in place and carries what earlier attempts
/// learned. Ported from <c>desktop_manager.restore_models.PlannedDesktopExecution</c>.
/// </summary>
/// <param name="desktop">The desktop.</param>
/// <param name="desktopIndex">Its number, from 1.</param>
/// <param name="closeExisting">Whether to close its windows before placing entries.</param>
public sealed class PlannedDesktopExecution(DesktopPlan desktop, int desktopIndex, bool closeExisting)
{
    /// <summary>The desktop.</summary>
    public DesktopPlan Desktop { get; } = desktop;

    /// <summary>Its number, from 1.</summary>
    public int DesktopIndex { get; } = desktopIndex;

    /// <summary>Whether to close its windows before placing entries; a retry turns this off.</summary>
    public bool CloseExisting { get; set; } = closeExisting;

    /// <summary>One step per enabled entry, in plan order.</summary>
    public IReadOnlyList<PlannedEntryExecution> EntrySteps { get; set; } = [];

    /// <summary>Open windows on the desktop no entry claims.</summary>
    public IReadOnlyList<LiveWindow> OrphanWindows { get; set; } = [];

    /// <summary>
    /// Entries the completion check already confirmed in their planned slot. A retry fixes what is still wrong:
    /// rerunning a satisfied entry relaunches an app already in place and lets it steal the window it matched.
    /// </summary>
    public HashSet<string> SatisfiedEntryNames { get; } = new(StringComparer.Ordinal);

    /// <summary>Entries this run already launched. Their process may still be starting, so a retry waits for it rather than spawning another.</summary>
    public HashSet<string> LaunchedEntryNames { get; } = new(StringComparer.Ordinal);
}

/// <summary>Everything a restore will do, desktop by desktop. Ported from <c>desktop_manager.restore_models.RestoreExecutionPlan</c>.</summary>
public sealed class RestoreExecutionPlan
{
    /// <summary>The desktops to restore, in plan order.</summary>
    public IReadOnlyList<PlannedDesktopExecution> Desktops { get; init; } = [];

    /// <summary>The one desktop asked for, if any.</summary>
    public string? TargetDesktop { get; init; }

    /// <summary>That desktop's number, from 1.</summary>
    public int? TargetDesktopIndex { get; init; }

    /// <summary>The steps as the dry run lists them.</summary>
    public IReadOnlyList<string> RenderActions()
    {
        var actions = new List<string>();
        foreach (var desktop in Desktops)
        {
            actions.Add($"Desktop {desktop.DesktopIndex}: {desktop.Desktop.Name}");
            if (desktop.CloseExisting)
            {
                actions.Add("  - close existing windows on this desktop");
            }

            foreach (var step in desktop.EntrySteps)
            {
                actions.Add(step switch
                {
                    { Action: EntryAction.Satisfied } => $"  - already in place `{step.Entry.Name}`",
                    { Action: EntryAction.Reuse, ReusableWindow: { } window } =>
                        $"  - reuse window `{step.Entry.Name}`" + Suffix(" from desktop ", window.DesktopNumber),
                    _ => $"  - {step.Description}",
                });
            }

            foreach (var orphan in desktop.OrphanWindows)
            {
                var label = string.IsNullOrEmpty(orphan.ProcessName) ? orphan.Title : $"{orphan.Title} [{orphan.ProcessName}]";
                actions.Add($"  - orphan open window `{label}`" + Suffix(" on desktop ", orphan.DesktopNumber) + Suffix(" screen ", orphan.ScreenNumber));
            }
        }

        return actions;
    }

    /// <summary>
    /// The 1-based position of the desktop named <paramref name="targetDesktop"/>, ignoring case; <see langword="null"/>
    /// when none is asked for. Ported from <c>desktop_manager.restore_planner.resolve_target_desktop_index</c>.
    /// </summary>
    /// <exception cref="ArgumentException">No desktop has that name.</exception>
    public static int? ResolveTargetDesktopIndex(WorkspacePlan plan, string? targetDesktop)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (targetDesktop is null)
        {
            return null;
        }

        var wanted = Vault.PyText.CaseFold(targetDesktop);
        for (var index = 0; index < plan.Desktops.Count; index++)
        {
            if (Vault.PyText.CaseFold(plan.Desktops[index].Name) == wanted)
            {
                return index + 1;
            }
        }

        var available = string.Join(", ", plan.Desktops.Select(desktop => desktop.Name));
        throw new ArgumentException($"Desktop '{targetDesktop}' not found in plan. Available: {available}");
    }

    private static string Suffix(string prefix, int? value) =>
        value is { } known ? prefix + known.ToString(CultureInfo.InvariantCulture) : string.Empty;
}
