#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Text;

namespace Rig.Cli.Desktop;

/// <summary>The machine commands: align, name-desktops and the display guard.</summary>
internal sealed partial class DesktopWorkspaceCommand
{
    private Command AlignCommand()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var desktop = DesktopOption("Only align the windows of one plan desktop by name.");
        var enable = EnableOption("Turn a plan feature on for this run. Repeatable.");
        var disable = DisableOption("Turn a plan feature off for this run. Repeatable.");
        var dryRun = DryRunOption("Report what would move without moving anything.");
        var verbose = new Option<bool>("--verbose", "-v") { Description = "List every plan entry's outcome." };
        var reconcile = new Option<bool>("--reconcile-desktops")
        {
            Description = "Ensure the correct virtual desktops exist and have the right names.",
            DefaultValueFactory = _ => true,
        };
        var noReconcile = new Option<bool>("--no-reconcile-desktops") { Description = "Block instead of reconciling when desktop names drift." };
        var command = new Command("align", "Move open plan windows back to their planned desktop and slot. Never launches or closes windows.")
        {
            config,
            desktop,
            enable,
            disable,
            dryRun,
            verbose,
            reconcile,
            noReconcile,
        };
        command.SetAction((parsed, cancellationToken) => AlignWindows(
            parsed.GetValue(config),
            parsed.GetValue(desktop),
            parsed.GetValue(enable) ?? [],
            parsed.GetValue(disable) ?? [],
            parsed.GetValue(dryRun),
            parsed.GetValue(verbose),
            parsed.GetValue(reconcile) && !parsed.GetValue(noReconcile),
            cancellationToken));
        return command;
    }

    private Command NameDesktops()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var dryRun = DryRunOption("Show the names that would be applied without setting them.");
        var command = new Command("name-desktops", "Ensure the declared virtual desktops exist and have the right names.")
        {
            config,
            dryRun,
        };
        command.SetAction((parsed, cancellationToken) => NameDesktopsCommand(parsed.GetValue(config), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private Command Guard()
    {
        var config = ConfigOption("Workspace plan path. Only used to find the logs directory shared with restore.");
        var poll = new Option<double>("--poll-seconds") { Description = "Seconds between topology checks.", DefaultValueFactory = _ => 1.0 };
        var settle = new Option<double>("--settle-seconds") { Description = "How long a topology must hold before windows are put back.", DefaultValueFactory = _ => 6.0 };
        var snapshot = new Option<double>("--snapshot-seconds") { Description = "Seconds between placement snapshots while displays are stable.", DefaultValueFactory = _ => 5.0 };
        var dryRun = DryRunOption("Log what would be moved without moving anything.");
        var command = new Command("guard", "Watch for monitors dropping out or the machine waking, and put windows back.")
        {
            config,
            poll,
            settle,
            snapshot,
            dryRun,
        };
        command.SetAction((parsed, cancellationToken) => GuardCommand(
            parsed.GetValue(config),
            parsed.GetValue(poll),
            parsed.GetValue(settle),
            parsed.GetValue(snapshot),
            parsed.GetValue(dryRun),
            cancellationToken));
        return command;
    }

    private async Task<int> AlignWindows(
        string? config,
        string? desktop,
        IReadOnlyList<string> enable,
        IReadOnlyList<string> disable,
        bool dryRun,
        bool verbose,
        bool reconcile,
        CancellationToken cancellationToken)
    {
        if (await WindowsOnly("workspace align").ConfigureAwait(false) is not { } machine)
        {
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        var path = ConfigPath(config);
        if (!File.Exists(path))
        {
            await host.Output.WriteLineAsync($"Config not found: {path}").ConfigureAwait(false);
            return 1;
        }

        var logDirectory = host.LogsDirectory(path);
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Join(logDirectory, "align.log");
        void Report(string message, bool echo)
        {
            File.AppendAllText(logPath, $"{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss} {message}\n");
            if (echo)
            {
                host.Output.WriteLine(message);
            }
        }

        WorkspacePlan plan;
        try
        {
            plan = Load(path, enable, disable);
        }
        catch (Exception error) when (error is InvalidPlanException or JsonInputException or UnknownFeatureException or IOException)
        {
            Report($"align: error: {error.Message}", echo: true);
            return 1;
        }

        var prefix = dryRun ? "align (dry run)" : "align";
        if (Placement.FindMonitorShortfall(plan, machine.Win32.Monitors().Count) is { } shortfall)
        {
            Report($"{prefix}: skipped (plan needs {shortfall.Required} screens, {shortfall.Live} detected)", echo: true);
            return 0;
        }

        using var restoreLock = DisplayGuardLocks.Hold(
            Path.Join(DisplayGuardLocks.Directory(host.AppData), DisplayGuardLocks.RestoreLockFileName),
            TimeSpan.FromMilliseconds(500),
            Thread.Sleep);
        if (restoreLock is null)
        {
            Report($"{prefix}: skipped (restore in progress)", echo: true);
            return 0;
        }

        AlignReport report;
        try
        {
            report = Align.Run(
                plan,
                new NativeAlignPorts(machine.Win32, machine.Actions, machine.Desktops),
                host.Substitutions(plan),
                targetDesktop: desktop,
                dryRun: dryRun,
                reconcileDesktops: reconcile,
                log: message => Report(message, echo: false));
        }
        catch (Exception error) when (error is ArgumentException or DesktopException or InvalidPlanException)
        {
            Report($"align: error: {error.Message}", echo: true);
            return 1;
        }

        if (report.DesktopReconciliation is { } reconciliation)
        {
            foreach (var rename in reconciliation.Renamed)
            {
                Report($"  desktop {rename.Index}: renamed '{rename.CurrentName}' -> '{rename.TargetName}'", echo: verbose);
            }

            foreach (var create in reconciliation.Created)
            {
                Report($"  desktop {create.Index}: created '{create.Name}'", echo: verbose);
            }

            foreach (var retired in reconciliation.Retired)
            {
                Report($"  desktop retired: '{retired}'", echo: verbose);
            }
        }

        foreach (var decision in report.Decisions)
        {
            if (decision.Outcome is AlignOutcome.InPlace)
            {
                if (verbose)
                {
                    await host.Output.WriteLineAsync($"  {decision.Describe()}").ConfigureAwait(false);
                }

                continue;
            }

            Report($"  {decision.Describe()}", echo: verbose);
        }

        Report(Align.SummaryLine(report, dryRun), echo: true);
        return report.Count(AlignOutcome.Failed) > 0 ? AlignMovesFailed : 0;
    }

    private async Task<int> NameDesktopsCommand(string? config, bool dryRun, CancellationToken cancellationToken)
    {
        if (await Open("workspace name-desktops", config).ConfigureAwait(false) is not Opened(var machine, var path, var plan))
        {
            return 1;
        }

        var targets = plan.Desktops.Select(desktop => desktop.Name).ToList();
        await host.Output.WriteLineAsync("Virtual Desktop Reconciliation (IAC)").ConfigureAwait(false);
        await host.Output.WriteLineAsync($"Config: {path} ({targets.Count} planned desktop(s))").ConfigureAwait(false);

        var result = DesktopReconcile.Reconcile(
            targets,
            machine.Desktops,
            plan.RetiredDesktopNames,
            TimeSpan.FromMilliseconds(plan.SwitchDelayMs),
            dryRun);
        if (dryRun)
        {
            if (!result.Plan.HasDrift)
            {
                await host.Output.WriteLineAsync("No drift detected: all virtual desktops match declared state.").ConfigureAwait(false);
            }

            foreach (var name in result.Plan.ToRetire)
            {
                await host.Output.WriteLineAsync($"  - remove switched-off desktop: {name}").ConfigureAwait(false);
            }

            foreach (var create in result.Plan.ToCreate)
            {
                await host.Output.WriteLineAsync($"  + create Desktop {create.Index} (\"{create.Name}\")").ConfigureAwait(false);
            }

            foreach (var rename in result.Plan.ToRename)
            {
                var current = rename.CurrentName.Length > 0 ? $"\"{rename.CurrentName}\"" : "(unnamed)";
                await host.Output.WriteLineAsync($"  ~ rename Desktop {rename.Index} ({current} -> \"{rename.TargetName}\")").ConfigureAwait(false);
            }

            foreach (var item in result.Plan.Unchanged)
            {
                await host.Output.WriteLineAsync($"  = unchanged Desktop {item.Index} (\"{item.Name}\")").ConfigureAwait(false);
            }

            foreach (var item in result.Plan.Unmanaged)
            {
                await host.Output.WriteLineAsync($"  ! unmanaged surplus Desktop {item.Index} (\"{item.Name}\")").ConfigureAwait(false);
            }

            await host.Output.WriteLineAsync(
                $"Plan: {result.Plan.ToRetire.Count} to remove, {result.Plan.ToCreate.Count} to create, "
                + $"{result.Plan.ToRename.Count} to rename, {result.Plan.Unchanged.Count} unchanged.").ConfigureAwait(false);
            await host.Output.WriteLineAsync("Dry run: no changes applied.").ConfigureAwait(false);
            return 0;
        }

        foreach (var retired in result.Retired)
        {
            await host.Output.WriteLineAsync($"  Removed switched-off desktop {retired}").ConfigureAwait(false);
        }

        foreach (var create in result.Created)
        {
            await host.Output.WriteLineAsync($"  Created Desktop {create.Index} (\"{create.Name}\")").ConfigureAwait(false);
        }

        foreach (var rename in result.Renamed)
        {
            await host.Output.WriteLineAsync(
                rename.Success
                    ? $"  Desktop {rename.Index} -> {rename.TargetName}"
                    : $"  Desktop {rename.Index} -> {rename.TargetName} (failed)").ConfigureAwait(false);
        }

        foreach (var item in result.Unchanged)
        {
            await host.Output.WriteLineAsync($"  Desktop {item.Index} -> {item.Name} (already matched)").ConfigureAwait(false);
        }

        if (!result.Converged)
        {
            await host.Output.WriteLineAsync($"Failed to rename {result.FailedRenames.Count} desktop(s).").ConfigureAwait(false);
            return 1;
        }

        await host.Output.WriteLineAsync($"All {targets.Count} virtual desktop(s) converged to declared state.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> GuardCommand(
        string? config,
        double pollSeconds,
        double settleSeconds,
        double snapshotSeconds,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (await WindowsOnly("workspace guard").ConfigureAwait(false) is not { } machine)
        {
            return 1;
        }

        if (!OperatingSystem.IsWindows())
        {
            return 1;
        }

        var path = ConfigPath(config);
        var logDirectory = host.LogsDirectory(path);
        Directory.CreateDirectory(logDirectory);
        var locks = DisplayGuardLocks.Directory(host.AppData);
        Directory.CreateDirectory(locks);
        var logPath = Path.Join(logDirectory, DisplayGuardLocks.GuardLogFileName);

        using var guardLock = DisplayGuardLocks.TryHold(Path.Join(locks, DisplayGuardLocks.GuardLockFileName));
        if (guardLock is null)
        {
            await host.Output.WriteLineAsync("Another display guard is already running; exiting.").ConfigureAwait(false);
            return 0;
        }

        void Log(string message)
        {
            File.AppendAllText(logPath, $"{TimeProvider.System.GetLocalNow():yyyy-MM-dd HH:mm:ss} {message}\n");
            host.Output.WriteLine(message);
        }

        NativeDisplayGuardPorts.MakePerMonitorDpiAware();
        using var events = new DisplayEventListener(Log);
        events.Start();
        var ports = new NativeDisplayGuardPorts(
            machine.Win32,
            machine.Desktops,
            Path.Join(locks, DisplayGuardLocks.RestoreLockFileName),
            Log,
            events);
        var settings = new DisplayGuardSettings
        {
            SettleSeconds = settleSeconds,
            SnapshotIntervalSeconds = snapshotSeconds,
            DryRun = dryRun,
        };
        var guard = new DisplayGuard(ports, settings);
        Log($"Display guard started (pid {Environment.ProcessId}{(dryRun ? ", dry run" : string.Empty)}).");
        try
        {
            DisplayGuard.Run(guard, pollSeconds, seconds => Thread.Sleep(TimeSpan.FromSeconds(seconds)), () => cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            Log("Display guard stopped.");
        }

        return 0;
    }
}
