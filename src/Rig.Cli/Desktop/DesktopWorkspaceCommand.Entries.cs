#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Text;

namespace Rig.Cli.Desktop;

/// <summary>The per-entry commands: detect an app, launch a file, test one entry and restart an app.</summary>
internal sealed partial class DesktopWorkspaceCommand
{
    private sealed record EntryLocation(int DesktopIndex, DesktopPlan Desktop, LaunchEntry Entry);

    private Command DetectApp()
    {
        var name = new Option<string?>("--name") { Description = "Name of the launch entry to check." };
        var desktop = DesktopOption("Only check entries on this desktop.");
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var command = new Command("detect-app", "Detect whether applications from the workspace plan are currently running.") { name, desktop, config };
        command.SetAction((parsed, cancellationToken) =>
            DetectAppCommand(parsed.GetValue(name), parsed.GetValue(desktop), parsed.GetValue(config), cancellationToken));
        return command;
    }

    private Command LaunchFile()
    {
        var entryFile = new Option<string>("--entry-file") { Description = "Path to a JSON file containing one launch entry.", Required = true };
        var config = ConfigOption("Optional workspace plan used to provide aliases and substitutions.");
        var dryRun = DryRunOption("Show what would happen without launching.");
        var command = new Command("launch-file", "Launch one entry from a standalone JSON file using the normal launch path.") { entryFile, config, dryRun };
        command.SetAction((parsed, cancellationToken) =>
            LaunchFileCommand(parsed.GetRequiredValue(entryFile), parsed.GetValue(config), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private Command TestItem()
    {
        var name = new Option<string>("--name") { Description = "Name of the launch entry to test.", Required = true };
        var desktop = DesktopOption("Name of the desktop containing the entry.");
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var dryRun = DryRunOption("Show what would happen without launching.");
        var command = new Command("test-item", "Launch and place a single entry for testing.") { name, desktop, config, dryRun };
        command.SetAction((parsed, cancellationToken) =>
            TestItemCommand(parsed.GetRequiredValue(name), parsed.GetValue(desktop), parsed.GetValue(config), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private Command RestartApp()
    {
        var name = new Option<string>("--name") { Description = "Name of the launch entry/app to restart (e.g. Fork).", Required = true };
        var desktop = DesktopOption("Only restart matching entries on this desktop. Defaults to every desktop.");
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var graceful = new Option<bool>("--graceful") { Description = "Request a polite close first, then force-kill survivors." };
        var dryRun = DryRunOption("Show what would happen without closing or launching.");
        var command = new Command("restart-app", "Force-close every window of an app type and re-open it in its planned placement.") { name, desktop, config, graceful, dryRun };
        command.SetAction((parsed, cancellationToken) =>
            RestartAppCommand(parsed.GetRequiredValue(name), parsed.GetValue(desktop), parsed.GetValue(config), parsed.GetValue(graceful), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private Command UpdateOrca()
    {
        var config = ConfigOption("Path to the workspace JSON plan. Defaults to the platform default.");
        var closeTimeout = new Option<double>("--close-timeout")
        {
            Description = "Seconds to let Orca quit gracefully before force-killing it.",
            DefaultValueFactory = _ => 60.0,
        };
        var dryRun = DryRunOption("Show what would happen without closing or installing.");
        var command = new Command("update-orca", "Apply a staged Orca update and relaunch every Orca profile in its planned placement.") { config, closeTimeout, dryRun };
        command.SetAction((parsed, cancellationToken) =>
            UpdateOrcaCommand(parsed.GetValue(config), parsed.GetValue(closeTimeout), parsed.GetValue(dryRun), cancellationToken));
        return command;
    }

    private async Task<int> DetectAppCommand(string? name, string? desktop, string? config, CancellationToken cancellationToken)
    {
        if (await Open("workspace detect-app", config).ConfigureAwait(false) is not Opened(var machine, _, var plan))
        {
            return 1;
        }

        var entries = FindEntries(plan, name, desktop);
        if (entries.Count == 0)
        {
            await host.Output.WriteLineAsync("No matching entries found in the plan.", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var inventory = machine.Inventory();
        var foundAny = false;
        foreach (var location in entries)
        {
            var processName = location.Entry.WindowProcessName;
            if (string.IsNullOrEmpty(processName))
            {
                await host.Output.WriteLineAsync($"  {location.Desktop.Name} / {location.Entry.Name}  - no process name configured", cancellationToken).ConfigureAwait(false);
                continue;
            }

            var handles = inventory.WindowsMatchingProcess(processName, currentDesktopOnly: false);
            if (handles.Count > 0)
            {
                foundAny = true;
                await host.Output.WriteLineAsync(
                    $"  * {location.Desktop.Name} / {location.Entry.Name}  running ({handles.Count} window{(handles.Count == 1 ? string.Empty : "s")})", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await host.Output.WriteLineAsync($"  - {location.Desktop.Name} / {location.Entry.Name}  not running", cancellationToken).ConfigureAwait(false);
            }
        }

        return foundAny ? 0 : 1;
    }

    private async Task<int> LaunchFileCommand(string entryFile, string? config, bool dryRun, CancellationToken cancellationToken)
    {
        if (await WindowsOnly("workspace launch-file").ConfigureAwait(false) is not { } machine)
        {
            return 1;
        }

        LaunchEntry entry;
        WorkspacePlan plan;
        try
        {
            entry = PlanFiles.ParseLaunchEntry(await File.ReadAllTextAsync(entryFile, cancellationToken).ConfigureAwait(false), entryFile) with { Placement = null };
            plan = config is not null ? Load(config, [], []) : new WorkspacePlan();
        }
        catch (Exception error) when (error is InvalidPlanException or JsonInputException or UnknownFeatureException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var substitutions = host.Substitutions(plan);
        await host.Output.WriteLineAsync($"  - {WindowReuse.DescribeLaunch(entry, substitutions)}", cancellationToken).ConfigureAwait(false);
        if (dryRun)
        {
            return 0;
        }

        try
        {
            _ = machine.Launching.Launch(entry, substitutions);
            await host.Output.WriteLineAsync($"Launched: {entry.Name}", cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (DesktopException error)
        {
            await host.Error.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> TestItemCommand(string name, string? desktop, string? config, bool dryRun, CancellationToken cancellationToken)
    {
        if (await Open("workspace test-item", config).ConfigureAwait(false) is not Opened(var machine, var path, var plan))
        {
            return 1;
        }

        var resolvedDesktop = desktop;
        if (resolvedDesktop is null)
        {
            var current = machine.Desktops.CurrentNumber;
            if (current < 1 || current > plan.Desktops.Count)
            {
                await host.Output.WriteLineAsync("Could not determine the current desktop.", cancellationToken).ConfigureAwait(false);
                return 1;
            }

            resolvedDesktop = plan.Desktops[current - 1].Name;
            await host.Output.WriteLineAsync($"Using current desktop: {resolvedDesktop}", cancellationToken).ConfigureAwait(false);
        }

        var location = FindEntries(plan, name, resolvedDesktop).FirstOrDefault();
        if (location is null)
        {
            await host.Output.WriteLineAsync($"Entry not found on desktop `{resolvedDesktop}`: {name}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var substitutions = host.Substitutions(plan);
        var entry = location.Entry;
        await host.Output.WriteLineAsync($"Desktop {location.DesktopIndex}: {location.Desktop.Name}", cancellationToken).ConfigureAwait(false);
        await host.Output.WriteLineAsync($"  - {WindowReuse.DescribeLaunch(entry, substitutions)}", cancellationToken).ConfigureAwait(false);
        if (dryRun)
        {
            return 0;
        }

        var log = new FileRunLog(host.LogsDirectory(path), host.Time);
        var port = machine.Port(host.Time);
        try
        {
            _ = port.EnsureCurrentDesktop(location.DesktopIndex, TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
            var token = StepTimer.ShouldForceTerminalRelaunch(entry) ? StepTimer.NewTerminalRestoreToken(entry) : null;
            _ = port.Launch(entry, substitutions, token);
            log.Log($"Launched single entry: {entry.Name}");
            if (token is not null)
            {
                log.Log($"Launched {entry.Name} with restore token {token}");
            }

            if (entry.Placement is not null)
            {
                var outcome = port.ApplyWindowPlacement(entry, new(TimeSpan.FromSeconds(10), location.DesktopIndex), log);
                log.Log($"Applied placement {entry.Placement.ToJson().ToJsonString()} to {entry.Name}: placed={outcome.Placed}");
            }

            await host.Output.WriteLineAsync($"\nLog: {log.RunPath}", cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (DesktopException error)
        {
            await host.Error.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> RestartAppCommand(
        string name,
        string? desktop,
        string? config,
        bool graceful,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        if (await Open("workspace restart-app", config).ConfigureAwait(false) is not Opened(var machine, var path, var plan))
        {
            return 1;
        }

        var matches = FindEntries(plan, name, desktop);
        if (matches.Count == 0)
        {
            await host.Output.WriteLineAsync($"App not found in plan: {name}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var inventory = machine.Inventory();
        var actions = new WindowActions(machine.Actions, inventory);
        var substitutions = host.Substitutions(plan);
        var log = dryRun ? null : new FileRunLog(host.LogsDirectory(path), host.Time);
        var port = machine.Port(host.Time);

        foreach (var location in matches)
        {
            var entry = location.Entry;
            if (dryRun)
            {
                await host.Output.WriteLineAsync(
                    $"Would restart {entry.Name} on {location.Desktop.Name}: "
                    + $"{WindowReuse.DescribeLaunch(entry, substitutions)}", cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                foreach (var handle in WindowHandlesFor(entry, inventory))
                {
                    if (graceful)
                    {
                        actions.RequestClose(handle);
                    }
                    else
                    {
                        var pid = machine.Win32.ProcessIdOf(handle);
                        if (pid == 0 || !actions.TerminateProcess(pid))
                        {
                            actions.RequestClose(handle);
                        }
                    }
                }

                if (graceful)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                    foreach (var handle in WindowHandlesFor(entry, inventory))
                    {
                        var pid = machine.Win32.ProcessIdOf(handle);
                        if (pid != 0)
                        {
                            _ = actions.TerminateProcess(pid);
                        }
                    }
                }

                await host.Output.WriteLineAsync($"Restarted {entry.Name} on {location.Desktop.Name}", cancellationToken).ConfigureAwait(false);
                log?.Log($"Restarted {entry.Name} on desktop {location.DesktopIndex} ({location.Desktop.Name})");
                _ = port.EnsureCurrentDesktop(location.DesktopIndex, TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
                _ = port.Launch(entry, substitutions);
                if (entry.Placement is not null)
                {
                    _ = port.ApplyWindowPlacement(entry, new(TimeSpan.FromSeconds(10), location.DesktopIndex), log ?? RunLog.Discard);
                }
            }
            catch (DesktopException error)
            {
                await host.Error.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
                return 1;
            }
        }

        if (log is not null)
        {
            await host.Output.WriteLineAsync($"\nLog: {log.RunPath}", cancellationToken).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> UpdateOrcaCommand(string? config, double closeTimeout, bool dryRun, CancellationToken cancellationToken)
    {
        if (await WindowsOnly("workspace update-orca").ConfigureAwait(false) is not { } machine)
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
            await host.Output.WriteLineAsync($"Config not found: {path}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        WorkspacePlan plan;
        try
        {
            plan = Load(path, [], []);
        }
        catch (Exception error) when (error is InvalidPlanException or JsonInputException or UnknownFeatureException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var inventory = machine.Inventory();
        var actions = new WindowActions(machine.Actions, inventory);
        var log = dryRun ? null : new FileRunLog(host.LogsDirectory(path), host.Time);
        var localAppData = host.Environment("LOCALAPPDATA") ?? Path.Join(host.Home, "AppData", "Local");

        OrcaUpdateResult result;
        try
        {
            result = OrcaUpdate.Apply(
                plan,
                inventory,
                actions,
                localAppData,
                dryRun,
                log ?? RunLog.Discard,
                TimeSpan.FromSeconds(closeTimeout));
        }
        catch (Exception error) when (error is DesktopException or IOException or UnauthorizedAccessException)
        {
            await host.Error.WriteLineAsync(error.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        foreach (var action in result.Actions)
        {
            await host.Output.WriteLineAsync(action, cancellationToken).ConfigureAwait(false);
        }

        if (!dryRun)
        {
            var port = machine.Port(host.Time);
            var substitutions = host.Substitutions(plan);
            foreach (var (desktopIndex, desktop, entry) in OrcaUpdate.Entries(plan))
            {
                try
                {
                    _ = port.EnsureCurrentDesktop(desktopIndex, TimeSpan.FromMilliseconds(plan.SwitchDelayMs));
                    _ = port.Launch(entry, substitutions);
                    if (entry.Placement is not null)
                    {
                        _ = port.ApplyWindowPlacement(entry, new(TimeSpan.FromSeconds(10), desktopIndex), log ?? RunLog.Discard);
                    }

                    await host.Output.WriteLineAsync($"  - relaunched `{entry.Name}` on {desktop.Name}", cancellationToken).ConfigureAwait(false);
                }
                catch (DesktopException error)
                {
                    await host.Output.WriteLineAsync($"  - could not relaunch `{entry.Name}` on {desktop.Name}: {error.Message}", cancellationToken).ConfigureAwait(false);
                    result = result with { Ok = false };
                }
            }

            if (log is not null)
            {
                await host.Output.WriteLineAsync($"\nLog: {log.RunPath}", cancellationToken).ConfigureAwait(false);
            }
        }

        return result.Ok ? 0 : 1;
    }

    private static List<EntryLocation> FindEntries(WorkspacePlan plan, string? name, string? desktop)
    {
        var wantedName = name is null ? null : UnicodeText.CaseFold(name);
        var wantedDesktop = desktop is null ? null : UnicodeText.CaseFold(desktop);
        var entries = new List<EntryLocation>();
        for (var index = 0; index < plan.Desktops.Count; index++)
        {
            var item = plan.Desktops[index];
            if (wantedDesktop is not null && UnicodeText.CaseFold(item.Name) != wantedDesktop)
            {
                continue;
            }

            entries.AddRange(item.Launch
                .Where(entry => wantedName is null || UnicodeText.CaseFold(entry.Name) == wantedName)
                .Select(entry => new EntryLocation(index + 1, item, entry)));
        }

        return entries;
    }

    private static IReadOnlyList<long> WindowHandlesFor(LaunchEntry entry, WindowInventory inventory) =>
        string.IsNullOrEmpty(entry.WindowProcessName)
            ? []
            : inventory.WindowsMatchingProcess(entry.WindowProcessName, currentDesktopOnly: false);
}
