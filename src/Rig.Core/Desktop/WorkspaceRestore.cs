namespace Rig.Core.Desktop;

/// <summary>How a restore runs.</summary>
/// <param name="Substitutions">The plan's placeholders, resolved by the caller from its environment and machine config.</param>
public sealed record RestoreOptions(IReadOnlyDictionary<string, string> Substitutions)
{
    /// <summary>List what would happen and touch nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Close each desktop's windows before placing its entries.</summary>
    public bool CloseExisting { get; init; }

    /// <summary>Reuse windows already open instead of launching every entry.</summary>
    public bool DiffExisting { get; init; }

    /// <summary>Restore only the desktop with this name.</summary>
    public string? TargetDesktop { get; init; }
}

/// <summary>
/// Restore a workspace plan onto the machine: set the desktops up, plan each one against the windows open now, then
/// reuse or launch every entry, retrying a desktop until it matches. Ported from
/// <c>desktop_manager.handlers.restore_handler</c>.
/// </summary>
public static class WorkspaceRestore
{
    /// <summary>Restore <paramref name="plan"/>; the actions taken, or for a dry run the ones that would be.</summary>
    public static IReadOnlyList<string> Run(WorkspacePlan plan, RestoreOptions options, IDesktopPort port, IRunLog log)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(log);
        using var span = StepTimer.Source.StartActivity("workspace.restore");
        span?.SetTag("scope", RestoreSteps.Scope);
        span?.SetTag("restore.dry_run", options.DryRun);
        span?.SetTag("restore.close_existing", options.CloseExisting);
        span?.SetTag("restore.diff_existing", options.DiffExisting);
        span?.SetTag("restore.target_desktop", options.TargetDesktop ?? "all");
        span?.SetTag("restore.desktop_count", plan.Desktops.Count);
        span?.SetTag("restore.retired_desktops", plan.RetiredDesktopNames.ToArray());
        var actions = Restore(plan, options, port, log);
        span?.SetTag("restore.action_count", actions.Count);
        return actions;
    }

    private static List<string> Restore(WorkspacePlan plan, RestoreOptions options, IDesktopPort port, IRunLog log)
    {
        if (plan.Desktops.Count == 0)
        {
            return ["No desktops configured."];
        }

        var steps = new StepTimer(log, RestoreSteps.Scope, () => port.Now);
        log.Log(
            $"Starting restore dry_run={PyBool(options.DryRun)} close_existing={PyBool(options.CloseExisting)} diff_existing={PyBool(options.DiffExisting)} "
            + $"desktops={options.TargetDesktop ?? "all"} workspace={plan.Workspace ?? "None"}");

        IReadOnlySet<long> preserved = options.DiffExisting || options.CloseExisting ? port.WindowHandlesToPreserve() : new HashSet<long>();
        if (preserved.Count > 0)
        {
            log.Log($"Preserving {preserved.Count} invoking window(s) from reuse/close handling");
        }

        int? targetIndex;
        try
        {
            targetIndex = RestoreExecutionPlan.ResolveTargetDesktopIndex(plan, options.TargetDesktop);
        }
        catch (ArgumentException missing)
        {
            return [missing.Message];
        }

        var run = new RestoreRun(plan, port, log, options.Substitutions, preserved, new());

        // A self-updating app (Warp) applied mid-restore kills the windows already placed, so updates settle before
        // any window is matched to an entry.
        int? startingDesktop = null;
        if (!options.DryRun)
        {
            using (steps.Step("restore.settle_app_updates"))
            {
                port.SettleAppUpdates(plan, log);
            }

            // Retiring a leftover desktop renumbers the ones after it, so the desktops are set up before planning.
            startingDesktop = PrepareDesktops(run, steps);
        }

        var execution = new RestoreExecutionPlan
        {
            Desktops =
            [
                .. plan.Desktops
                    .Select((desktop, index) => new PlannedDesktopExecution(desktop, index + 1, options.CloseExisting))
                    .Where(desktop => targetIndex is null || desktop.DesktopIndex == targetIndex),
            ],
            TargetDesktop = options.TargetDesktop,
            TargetDesktopIndex = targetIndex,
        };

        using (steps.Step("restore.plan"))
        {
            var planning = options.DryRun ? run.Quiet() : run;
            foreach (var desktop in execution.Desktops)
            {
                RestoreSteps.RefreshPlan(planning, desktop, options.DiffExisting);
            }
        }

        var actions = RetireActions(plan);
        actions.AddRange(execution.RenderActions());
        if (options.DryRun)
        {
            return actions;
        }

        // CLI self-updates start up front so their runtime overlaps the restore instead of blocking each terminal.
        using (steps.Step("restore.start_terminal_command_upgrades"))
        {
            port.StartTerminalCommandUpgrades(plan);
        }

        log.Log("Started terminal command upgrades in the background");
        if (targetIndex is { } target)
        {
            port.EnsureCurrentDesktop(target, run.SwitchSettle);
            log.Log($"Navigated directly to desktop {target}");
        }
        else
        {
            port.SwitchLeftmost(plan.Desktops.Count + 2);
            log.Log("Switched to the leftmost desktop");
            port.Sleep(run.SwitchSettle);
            port.EnsureCurrentDesktop(1, run.SwitchSettle);
            log.Log("Confirmed desktop 1 is active before restore");
        }

        var secondPass = new Dictionary<int, IReadOnlyList<LaunchEntry>>();
        int? previous = null;
        foreach (var desktop in execution.Desktops)
        {
            IReadOnlyList<LaunchEntry> pending;
            using (var step = steps.Step(
                "restore.desktop",
                label: $"desktop {desktop.DesktopIndex} ({desktop.Desktop.Name})",
                slowAfter: StepTimer.SlowDesktop,
                tags: [new("desktop.index", desktop.DesktopIndex), new("desktop.name", desktop.Desktop.Name)]))
            {
                pending = RestoreDesktop(run, desktop, previous, targetIndex);
                step.Set("desktop.pending_second_pass", pending.Count);
            }

            if (pending.Count > 0)
            {
                secondPass[desktop.DesktopIndex] = pending;
            }

            previous = desktop.DesktopIndex;
        }

        if (run.Fallbacks.HasMissingPaths)
        {
            using (steps.Step("restore.missing_path_fallbacks"))
            {
                actions.AddRange(port.OpenMissingPathFallbacks(run.Fallbacks, log));
            }
        }

        if (secondPass.Count > 0)
        {
            var count = secondPass.Values.Sum(entries => entries.Count);
            using (steps.Step("restore.second_pass", tags: [new("entry.count", count)]))
            {
                actions.AddRange(SecondPass(run, secondPass, count, targetIndex));
            }
        }
        else
        {
            log.Log("First pass fully successful; skipped second-pass placement");
        }

        if (startingDesktop is { } start)
        {
            bool returned;
            using (var step = steps.Step("restore.return_to_start", tags: [new("desktop.index", start)]))
            {
                returned = port.EnsureCurrentDesktop(start, run.SwitchSettle);
                step.Set("desktop.confirmed", returned);
            }

            actions.Add($"Return to starting desktop {start}");
            log.Log($"Returned to starting desktop {start} ({(returned ? "confirmed" : "best-effort only")})");
        }

        steps.LogSummary();
        log.Log("Restore finished successfully");
        return actions;
    }

    // Retire switched-off desktops, top the count up, and return the starting desktop.
    private static int? PrepareDesktops(RestoreRun run, StepTimer steps)
    {
        var (plan, port, log) = (run.Plan, run.Port, run.Log);
        port.EnsureAvailable();
        log.Log("Ensured Windows environment is available");
        var starting = port.CurrentDesktopNumber();
        log.Log($"Captured starting desktop: {RestoreSteps.PidOrUnknown(starting)}");

        // Retire before topping up: a leftover can sit among the planned desktops, and the gap it leaves is then filled.
        if (plan.RetiredDesktopNames.Count > 0)
        {
            IReadOnlyList<string> retired;
            using (var step = steps.Step("restore.retire_desktops", tags: [new("desktop.retire_candidates", plan.RetiredDesktopNames.ToArray())]))
            {
                retired = port.RetireSwitchedOffDesktops(plan.Desktops.Count, plan.RetiredDesktopNames);
                step.Set("desktop.retired", retired.ToArray());
            }

            // Removing desktops renumbers the ones after them, and starting on a removed one moves to the fallback, so
            // the desktop restore returns to is read again.
            if (retired.Count > 0)
            {
                log.Log($"Removed switched-off desktop(s): {string.Join(", ", retired)}");
                starting = port.CurrentDesktopNumber();
            }
        }

        using (steps.Step("restore.ensure_desktop_count", tags: [new("desktop.count", plan.Desktops.Count)]))
        {
            port.EnsureDesktopCount(plan.Desktops.Count, run.SwitchSettle);
        }

        log.Log($"Ensured desktop count is at least {plan.Desktops.Count}");
        return starting;
    }

    private static List<string> RetireActions(WorkspacePlan plan) =>
        plan.RetiredDesktopNames.Count == 0 ? [] : [$"Remove switched-off desktop(s), if present: {string.Join(", ", plan.RetiredDesktopNames)}"];

    private static IReadOnlyList<LaunchEntry> RestoreDesktop(RestoreRun run, PlannedDesktopExecution desktop, int? previous, int? targetIndex)
    {
        Activate(run, desktop, previous, targetIndex);
        var current = run.Port.CurrentDesktopStatus(desktop.Desktop.Name);
        run.Log.Log($"Captured desktop {desktop.DesktopIndex} status before restore: {current.CapturedWindows.Count} visible window(s)");
        return RestoreSteps.ExecuteWithRetries(run, desktop);
    }

    private static void Activate(RestoreRun run, PlannedDesktopExecution desktop, int? previous, int? targetIndex)
    {
        if (targetIndex is null && previous is { } before && desktop.DesktopIndex > before)
        {
            run.Port.MoveRight(desktop.DesktopIndex - before);
            run.Log.Log($"Moved to desktop {desktop.DesktopIndex}");
            run.Port.Sleep(run.SwitchSettle);
        }

        var confirmed = run.Port.EnsureCurrentDesktop(desktop.DesktopIndex, run.SwitchSettle);
        run.Log.Log($"Desktop {desktop.DesktopIndex} activation {RestoreSteps.Anchored(confirmed)} before restore");
        bool renamed;
        using (var span = StepTimer.Source.StartActivity("restore.rename_desktop"))
        {
            span?.SetTag("scope", RestoreSteps.Scope);
            span?.SetTag("desktop.index", desktop.DesktopIndex);
            span?.SetTag("desktop.name", desktop.Desktop.Name);
            renamed = run.Port.RenameDesktop(desktop.DesktopIndex, desktop.Desktop.Name);
            span?.SetTag("desktop.renamed", renamed);
        }

        if (!renamed)
        {
            run.Log.Log($"Desktop {desktop.DesktopIndex} could not be renamed to '{desktop.Desktop.Name}'");
        }
    }

    private static IReadOnlyList<string> SecondPass(RestoreRun run, Dictionary<int, IReadOnlyList<LaunchEntry>> pending, int count, int? targetIndex)
    {
        if (targetIndex is { } target)
        {
            run.Port.EnsureCurrentDesktop(target, run.SwitchSettle);
            run.Log.Log($"Navigated to desktop {target} for second-pass placement");
        }
        else
        {
            run.Port.SwitchLeftmost(run.Plan.Desktops.Count + 2);
            run.Log.Log("Returned to the leftmost desktop");
        }

        run.Log.Log($"Second pass needed for {count} entry(s)");
        run.Port.Sleep(run.SwitchSettle);
        return run.Port.ApplySecondPassPlacements(run.Plan, pending, run.Log);
    }

    private static string PyBool(bool value) => value ? "True" : "False";
}
