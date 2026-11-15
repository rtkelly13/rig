#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;

namespace Rig.Cli.Desktop;

/// <summary>The plan-reading commands: restore, audit, status, validation, features, export and machine wiring.</summary>
internal sealed partial class DesktopWorkspaceCommand
{
    private Command Restore()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var desktop = DesktopOption("Only restore a single desktop by name (e.g. Personal, Work, Admin).");
        var screen = ScreenOption("Only restore entries placed on the specified screen.");
        var enable = EnableOption("Turn a plan feature on for this run. Repeatable.");
        var disable = DisableOption("Turn a plan feature off for this run. Repeatable.");
        var close = new Option<bool>("--close") { Description = "Close existing windows before restoring." };
        var strict = new Option<bool>("--strict") { Description = "Fail if the plan does not fully cover targeted screens." };
        var relaunch = new Option<bool>("--relaunch") { Description = "Disable diff-based reuse; relaunch everything." };
        var dryRun = DryRunOption("Show what would happen without launching anything.");
        var defer = new Option<bool>("--defer-logon-commands")
        {
            Description = "Open terminals marked command_at_logon deferred as bare shells, for claude-sessions startup to fill.",
        };
        var wait = new Option<bool>("--wait-for-success")
        {
            Description = "After restore, check every few seconds whether the UI state still matches the plan.",
        };
        var poll = new Option<double>("--wait-poll-seconds") { Description = "Seconds between wait-for-success checks.", DefaultValueFactory = _ => 10.0 };
        var maxChecks = new Option<int>("--wait-max-checks") { Description = "Maximum wait-for-success checks.", DefaultValueFactory = _ => 18 };
        var afterSuccess = new Option<int>("--wait-checks-after-success") { Description = "Additional checks after the first success.", DefaultValueFactory = _ => 3 };
        var command = new Command("restore", "Restore the workspace from a saved plan.")
        {
            config,
            desktop,
            screen,
            enable,
            disable,
            close,
            strict,
            relaunch,
            dryRun,
            defer,
            wait,
            poll,
            maxChecks,
            afterSuccess,
        };
        command.SetAction((parsed, cancellationToken) => Restore(
            parsed.GetValue(config),
            parsed.GetValue(desktop),
            parsed.GetValue(screen),
            parsed.GetValue(enable) ?? [],
            parsed.GetValue(disable) ?? [],
            parsed.GetValue(close),
            parsed.GetValue(strict),
            parsed.GetValue(relaunch),
            parsed.GetValue(dryRun),
            parsed.GetValue(defer),
            parsed.GetValue(wait),
            parsed.GetValue(poll),
            parsed.GetValue(maxChecks),
            parsed.GetValue(afterSuccess),
            cancellationToken));
        return command;
    }

    private Command Audit()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var desktop = DesktopOption("Only audit a single desktop by name.");
        var screen = ScreenOption("Only audit entries placed on the specified screen.");
        var enable = EnableOption("Audit against the plan with this feature on. Repeatable.");
        var disable = DisableOption("Audit against the plan with this feature off. Repeatable.");
        var command = new Command("audit", "Compare the saved workspace plan to the live desktop state without changing anything.")
        {
            config,
            desktop,
            screen,
            enable,
            disable,
        };
        command.SetAction((parsed, cancellationToken) =>
            Audit(parsed.GetValue(config), parsed.GetValue(desktop), parsed.GetValue(screen), parsed.GetValue(enable) ?? [], parsed.GetValue(disable) ?? [], cancellationToken));
        return command;
    }

    private Command Status()
    {
        var verbose = new Option<bool>("--verbose", "-v") { Description = "Show process names and executable paths." };
        var command = new Command("status", "Show current desktop and window status.") { verbose };
        command.SetAction((parsed, cancellationToken) => Status(parsed.GetValue(verbose), cancellationToken));
        return command;
    }

    private Command ValidatePlan()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var strict = new Option<bool>("--strict") { Description = "Treat overlapping screen regions as errors." };
        var enable = EnableOption("Validate the plan with this feature on. Repeatable.");
        var disable = DisableOption("Validate the plan with this feature off. Repeatable.");
        var command = new Command("validate-plan", "Validate the workspace plan JSON.") { config, strict, enable, disable };
        command.SetAction((parsed, cancellationToken) =>
            ValidatePlan(parsed.GetValue(config), parsed.GetValue(strict), parsed.GetValue(enable) ?? [], parsed.GetValue(disable) ?? [], cancellationToken));
        return command;
    }

    private Command Features()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var command = new Command("features", "List the plan's feature toggles, their default state, and what sits behind each.") { config };
        command.SetAction((parsed, cancellationToken) => Features(parsed.GetValue(config), cancellationToken));
        return command;
    }

    private Command ExportPlan()
    {
        var config = ConfigOption("Output path for the saved plan. Defaults to the platform default.");
        var workspace = new Option<string?>("--workspace") { Description = "Workspace root used to convert desktop 1 terminals into launch commands." };
        var count = new Option<int?>("--desktop-count") { Description = "Override the detected virtual desktop count when exporting." };
        var bundle = new Option<bool>("--bundle") { Description = "Export a bundle root plus one file per desktop." };
        var directory = new Option<string>("--desktop-dir") { Description = "Relative directory for per-desktop files when --bundle is set.", DefaultValueFactory = _ => "desktops" };
        var command = new Command("export-plan", "Capture the current desktop layout and export a draft restore plan.")
        {
            config,
            workspace,
            count,
            bundle,
            directory,
        };
        command.SetAction((parsed, cancellationToken) => ExportPlan(
            parsed.GetValue(config),
            parsed.GetValue(workspace),
            parsed.GetValue(count),
            parsed.GetValue(bundle),
            parsed.GetValue(directory) ?? "desktops",
            cancellationToken));
        return command;
    }

    private Command WireMachine()
    {
        var workspace = new Option<string>("--workspace") { Description = "Root directory for the primary workspace.", Required = true };
        var personal = new Option<string?>("--personal-workspace") { Description = "Root directory for the personal workspace." };
        var config = ConfigOption("Output path for the saved plan. Defaults to the platform default.");
        var command = new Command("wire-machine", "Generate and save the machine workspace plan from the repository's template.") { workspace, personal, config };
        command.SetAction((parsed, cancellationToken) =>
            WireMachine(parsed.GetRequiredValue(workspace), parsed.GetValue(personal), parsed.GetValue(config), cancellationToken));
        return command;
    }

    private async Task<int> Restore(
        string? config,
        string? desktop,
        int? screen,
        IReadOnlyList<string> enable,
        IReadOnlyList<string> disable,
        bool close,
        bool strict,
        bool relaunch,
        bool dryRun,
        bool defer,
        bool waitForSuccess,
        double pollSeconds,
        int maxChecks,
        int checksAfterSuccess,
        CancellationToken cancellationToken)
    {
        var native = host.Windows();
        var denied = await WindowsOnly(native, "workspace restore").ConfigureAwait(false);
        if (denied != 0)
        {
            return denied;
        }

        var path = ConfigPath(config);
        if (!File.Exists(path))
        {
            await host.Output.WriteLineAsync($"Config not found: {path}").ConfigureAwait(false);
            return 1;
        }

        WorkspacePlan plan;
        try
        {
            plan = Load(path, enable, disable);
        }
        catch (Exception error) when (error is InvalidPlanException or UnknownFeatureException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }

        if (defer && FlagOn("claude-session-restore"))
        {
            plan = PlanFeatures.DeferLogonCommands(plan);
        }

        FeatureLine(plan);
        if (waitForSuccess && maxChecks < 1)
        {
            await host.Output.WriteLineAsync("--wait-max-checks must be at least 1.").ConfigureAwait(false);
            return 2;
        }

        var machine = native!;
        var liveScreens = machine.Win32.Monitors().Count;
        if (!dryRun && desktop is null && screen is null
            && Placement.FindMonitorShortfall(plan, liveScreens) is { } shortfall)
        {
            var note = Path.Join(host.LogsDirectory(path), "monitor-shortfall.txt");
            Directory.CreateDirectory(host.LogsDirectory(path));
            await File.WriteAllTextAsync(
                note,
                $"Workspace restore skipped: plan needs {shortfall.Required} screens, only {shortfall.Live} detected.\n",
                cancellationToken).ConfigureAwait(false);
            await host.Output.WriteLineAsync(
                $"Workspace restore skipped: plan needs {shortfall.Required} screens, only {shortfall.Live} detected. Note: {note}.").ConfigureAwait(false);
            return RestoreDeferred;
        }

        var scoped = WorkspaceScope.For(plan, desktop, screen);
        if (strict)
        {
            var issues = Placement.ValidatePlanStructure(scoped, expectedScreenCount: screen is null ? null : 0, strict: true);
            if (issues.Count > 0)
            {
                foreach (var issue in issues)
                {
                    await host.Output.WriteLineAsync($"Structure issue: {issue}").ConfigureAwait(false);
                }

                return 1;
            }
        }

        var lockPath = Path.Join(DisplayGuardLocks.Directory(host.AppData), DisplayGuardLocks.RestoreLockFileName);
        using var restoreLock = dryRun ? null : DisplayGuardLocks.Hold(lockPath, TimeSpan.FromSeconds(2), Thread.Sleep);
        if (!dryRun && restoreLock is null)
        {
            await host.Output.WriteLineAsync($"Another workspace restore is already running ({lockPath}).").ConfigureAwait(false);
            return 1;
        }

        var log = dryRun ? null : new FileRunLog(host.LogsDirectory(path), host.Time);
        var port = new NativeDesktopPort(machine.Win32, machine.Actions, machine.Desktops, machine.Launching, host.Time);
        var options = new RestoreOptions(host.Substitutions(plan))
        {
            DryRun = dryRun,
            CloseExisting = close,
            DiffExisting = !relaunch,
            TargetDesktop = desktop,
        };
        try
        {
            var actions = WorkspaceRestore.Run(plan with { Desktops = WorkspaceScope.For(plan, null, screen).Desktops }, options, port, log ?? RunLog.Discard);
            foreach (var action in actions)
            {
                await host.Output.WriteLineAsync(action).ConfigureAwait(false);
            }

            if (!dryRun)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                var actual = CurrentStatusForScope(port, plan, desktop, screen);
                await host.Output.WriteLineAsync("\nFinal Status").ConfigureAwait(false);
                await host.Output.WriteLineAsync(WorkspaceDiff.Of(scoped, actual, host.Substitutions(plan)).Render()).ConfigureAwait(false);
            }

            if (!dryRun && waitForSuccess
                && !await WaitForRestoreSuccess(port, plan, desktop, screen, log, pollSeconds, maxChecks, checksAfterSuccess, cancellationToken).ConfigureAwait(false))
            {
                return 1;
            }

            if (log is not null)
            {
                await host.Output.WriteLineAsync($"\nLog: {log.RunPath}").ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception error) when (error is DesktopException or InvalidPlanException or IOException)
        {
            await host.Error.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> Audit(string? config, string? desktop, int? screen, IReadOnlyList<string> enable, IReadOnlyList<string> disable, CancellationToken cancellationToken)
    {
        var native = host.Windows();
        var denied = await WindowsOnly(native, "workspace audit").ConfigureAwait(false);
        if (denied != 0)
        {
            return denied;
        }

        var path = ConfigPath(config);
        if (!File.Exists(path))
        {
            await host.Output.WriteLineAsync($"Config not found: {path}").ConfigureAwait(false);
            return 1;
        }

        WorkspacePlan plan;
        try
        {
            plan = Load(path, enable, disable);
        }
        catch (Exception error) when (error is InvalidPlanException or UnknownFeatureException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }

        var scoped = WorkspaceScope.For(plan, desktop, screen);
        var port = new NativeDesktopPort(native!.Win32, native.Actions, native.Desktops, native.Launching, host.Time);
        var actual = CurrentStatusForScope(port, plan, desktop, screen);
        var diff = WorkspaceDiff.Of(scoped, actual, host.Substitutions(plan));
        await host.Output.WriteLineAsync("Workspace Audit").ConfigureAwait(false);
        await host.Output.WriteLineAsync(diff.Render()).ConfigureAwait(false);
        await host.Output.WriteLineAsync(diff.IsClean ? "Audit passed." : "Audit found drift.").ConfigureAwait(false);
        return diff.IsClean ? 0 : 1;
    }

    private async Task<int> Status(bool verbose, CancellationToken cancellationToken)
    {
        var native = host.Windows();
        var denied = await WindowsOnly(native, "workspace status").ConfigureAwait(false);
        if (denied != 0)
        {
            return denied;
        }

        var machine = native!;
        var port = new NativeDesktopPort(machine.Win32, machine.Actions, machine.Desktops, machine.Launching, host.Time);
        var status = port.CurrentWorkspaceStatus();
        for (var index = 0; index < status.Desktops.Count; index++)
        {
            var item = status.Desktops[index];
            if (index > 0)
            {
                await host.Output.WriteLineAsync().ConfigureAwait(false);
            }

            await host.Output.WriteLineAsync($"Desktop: {item.Name}").ConfigureAwait(false);
            if (item.CapturedWindows.Count == 0)
            {
                await host.Output.WriteLineAsync("  (no visible windows)").ConfigureAwait(false);
                continue;
            }

            foreach (var window in item.CapturedWindows)
            {
                var line = $"  - {window.Title}";
                if (window.ScreenNumber is { } screenNumber)
                {
                    line += $"  screen {screenNumber}";
                }

                if (verbose && !string.IsNullOrEmpty(window.ProcessName))
                {
                    line += $"  ({window.ProcessName})";
                }

                if (verbose && !string.IsNullOrEmpty(window.ExePath))
                {
                    line += $"  {window.ExePath}";
                }

                await host.Output.WriteLineAsync(line).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private async Task<int> ValidatePlan(string? config, bool strict, IReadOnlyList<string> enable, IReadOnlyList<string> disable, CancellationToken cancellationToken)
    {
        var path = ConfigPath(config);
        if (!File.Exists(path))
        {
            await host.Output.WriteLineAsync($"Config not found: {path}").ConfigureAwait(false);
            return 1;
        }

        WorkspacePlan plan;
        try
        {
            plan = LoadContext(path, enable, disable);
        }
        catch (Exception error) when (error is InvalidPlanException or UnknownFeatureException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }

        var monitorCount = host.Windows() is { } native && OperatingSystem.IsWindows() ? native.Win32.Monitors().Count : 0;
        var errors = Placement.ValidatePlan(plan, host.Substitutions(plan), monitorCount, new LocalPathProbe());
        var issues = Placement.ValidatePlanStructure(plan, strict: strict);
        foreach (var error in errors)
        {
            await host.Output.WriteLineAsync($"Error: {error}").ConfigureAwait(false);
        }

        foreach (var issue in issues)
        {
            await host.Output.WriteLineAsync($"Structure: {issue}").ConfigureAwait(false);
        }

        if (errors.Count == 0 && issues.Count == 0)
        {
            await host.Output.WriteLineAsync("Plan is valid.").ConfigureAwait(false);
            return 0;
        }

        return 1;
    }

    private async Task<int> Features(string? config, CancellationToken cancellationToken)
    {
        var path = ConfigPath(config);
        if (!File.Exists(path))
        {
            await host.Output.WriteLineAsync($"Config not found: {path}").ConfigureAwait(false);
            return 1;
        }

        WorkspacePlan parsed;
        WorkspacePlan plan;
        HashSet<string> overridden;
        try
        {
            parsed = PlanFiles.Load(path);
            var states = host.PlanFlags();
            plan = PlanFeatures.WithFlags(parsed, states);
            overridden = [.. states.Where(state => state.Override is not null).Select(state => state.Name)];
        }
        catch (Exception error) when (error is InvalidPlanException or IOException)
        {
            await host.Output.WriteLineAsync($"Feature error: {error.Message}").ConfigureAwait(false);
            return 1;
        }

        await host.Output.WriteLineAsync("Workspace Features (defaults and overrides: `rig flags list`)").ConfigureAwait(false);
        foreach (var (name, enabled) in plan.Features.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var source = overridden.Contains(name) ? "rig flags override" : parsed.Features.ContainsKey(name) ? "plan" : "default";
            await host.Output.WriteLineAsync($"\n  {name}: {(enabled ? "on" : "off (paused)")} ({source})").ConfigureAwait(false);
            foreach (var desktop in plan.Desktops)
            {
                if (desktop.Feature == name)
                {
                    await host.Output.WriteLineAsync($"    - {desktop.Name}: whole desktop").ConfigureAwait(false);
                }

                foreach (var entry in desktop.Launch.Where(entry => entry.Feature == name))
                {
                    await host.Output.WriteLineAsync($"    - {desktop.Name}: {entry.Name}").ConfigureAwait(false);
                }
            }
        }

        var unowned = plan.Desktops
            .SelectMany(desktop => desktop.Launch
                .Where(entry => entry.Feature is not null && !plan.Features.ContainsKey(entry.Feature))
                .Select(entry => $"{desktop.Name}/{entry.Name}"))
            .ToList();
        unowned.AddRange(plan.Desktops
            .Where(desktop => desktop.Feature is not null && !plan.Features.ContainsKey(desktop.Feature))
            .Select(desktop => $"{desktop.Name} (whole desktop)"));
        if (unowned.Count > 0)
        {
            await host.Output.WriteLineAsync("\nReferencing undeclared features (treated as off):").ConfigureAwait(false);
            foreach (var item in unowned)
            {
                await host.Output.WriteLineAsync($"  - {item}").ConfigureAwait(false);
            }

            return 1;
        }

        return 0;
    }

    private async Task<int> ExportPlan(
        string? config,
        string? workspace,
        int? desktopCount,
        bool bundle,
        string desktopDirectory,
        CancellationToken cancellationToken)
    {
        var native = host.Windows();
        var denied = await WindowsOnly(native, "workspace export-plan").ConfigureAwait(false);
        if (denied != 0)
        {
            return denied;
        }

        var machine = native!;
        var inventory = new WindowInventory(machine.Win32, machine.Desktops);
        var count = desktopCount ?? machine.Desktops.Count;
        if (count < 1)
        {
            await host.Output.WriteLineAsync("Desktop count must be at least 1.").ConfigureAwait(false);
            return 1;
        }

        var path = ConfigPath(config);
        var plan = WorkspaceStatus.Capture(inventory, count, workspace);
        if (bundle)
        {
            PlanFiles.SaveBundle(plan, path, desktopDirectory);
        }
        else
        {
            PlanFiles.Save(plan, path);
        }

        await host.Output.WriteLineAsync($"Exported workspace plan: {path}").ConfigureAwait(false);
        await host.Output.WriteLineAsync($"Mode: {(bundle ? "bundle" : "single file")}").ConfigureAwait(false);
        if (!string.IsNullOrEmpty(workspace))
        {
            await host.Output.WriteLineAsync($"Workspace: {workspace}").ConfigureAwait(false);
        }

        if (desktopCount is not null)
        {
            await host.Output.WriteLineAsync($"Desktop count: {desktopCount}").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> WireMachine(string workspace, string? personalWorkspace, string? config, CancellationToken cancellationToken)
    {
        var path = ConfigPath(config);
        var template = Path.Join(host.Checkout, "desktop-manager", "workspace-plan.bundle.json");
        try
        {
            var plan = PlanLoading.BuildMachinePlan(template, workspace, personalWorkspace);
            PlanFiles.SaveBundle(plan, path);
            await host.Output.WriteLineAsync($"Saved workspace plan: {path}").ConfigureAwait(false);
            await host.Output.WriteLineAsync($"Workspace: {workspace}").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(personalWorkspace))
            {
                await host.Output.WriteLineAsync($"Personal workspace: {personalWorkspace}").ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception error) when (error is InvalidPlanException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private static WorkspacePlan CurrentStatusForScope(NativeDesktopPort port, WorkspacePlan plan, string? desktop, int? screen)
    {
        WorkspacePlan actual;
        if (desktop is not null)
        {
            var indexes = Enumerable.Range(1, plan.Desktops.Count)
                .Where(index => Rig.Core.Vault.PyText.CaseFold(plan.Desktops[index - 1].Name) == Rig.Core.Vault.PyText.CaseFold(desktop))
                .ToList();
            actual = port.CurrentWorkspaceStatus(indexes);
        }
        else
        {
            actual = port.CurrentWorkspaceStatus(plan.Desktops.Count);
        }

        return WorkspaceScope.ToScreen(actual, screen);
    }

    private async Task<bool> WaitForRestoreSuccess(
        NativeDesktopPort port,
        WorkspacePlan plan,
        string? desktop,
        int? screen,
        FileRunLog? log,
        double pollSeconds,
        int maxChecks,
        int checksAfterSuccess,
        CancellationToken cancellationToken)
    {
        var target = WorkspaceScope.For(plan, desktop, screen);
        var substitutions = host.Substitutions(plan);
        var sawSuccess = false;
        int? remaining = null;
        WorkspaceDiff? previous = null;
        for (var check = 1; check <= maxChecks; check++)
        {
            var actual = CurrentStatusForScope(port, plan, desktop, screen);
            var diff = WorkspaceDiff.Of(target, actual, substitutions);
            var success = diff.IsClean;
            var changed = previous is null || !diff.Equals(previous);
            await host.Output.WriteLineAsync($"Check {check}: {(success ? "success" : "not yet stable")}").ConfigureAwait(false);
            if (changed)
            {
                await host.Output.WriteLineAsync(diff.ChangedSince(previous).Render()).ConfigureAwait(false);
            }
            else
            {
                await host.Output.WriteLineAsync("No Change").ConfigureAwait(false);
            }

            log?.Log($"Wait-for-success check {check}: {(success ? "success" : "not yet stable")} changed={(changed ? "yes" : "no")}");
            if (changed && log is not null)
            {
                foreach (var line in diff.Render().Split('\n'))
                {
                    log.Log($"WAIT {line}");
                }
            }

            if (success)
            {
                sawSuccess = true;
                remaining = remaining is null ? checksAfterSuccess : remaining - 1;
                if (remaining <= 0)
                {
                    return true;
                }
            }
            else if (sawSuccess)
            {
                await host.Output.WriteLineAsync($"Regression detected after initial success on check {check}.").ConfigureAwait(false);
                log?.Log($"Wait-for-success regression detected on check {check}");
                return false;
            }

            previous = diff;
            if (check < maxChecks)
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), cancellationToken).ConfigureAwait(false);
            }
        }

        return sawSuccess;
    }

    private bool FlagOn(string name)
    {
        var state = host.PlanFlags().FirstOrDefault(flag => flag.Name == name);
        return state is not null && (state.Override ?? state.Default);
    }
}
