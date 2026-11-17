#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using Rig.Core.Desktop;
using Rig.Core.Desktop.Windows;
using Rig.Core.Flags;
using Rig.Core.Workspace;

namespace Rig.Cli.Desktop;

/// <summary>The machine's Windows pieces, opened lazily so <c>--help</c> works anywhere.</summary>
/// <param name="Win32">The Win32 reads.</param>
/// <param name="Actions">The Win32 writes.</param>
/// <param name="Desktops">The virtual desktops.</param>
/// <param name="Launching">How plan entries are started.</param>
[ExcludeFromCodeCoverage(Justification = "Desktop native wiring requires a live Windows desktop environment.")]
internal sealed record DesktopNative(IWin32Windows Win32, IWin32Actions Actions, IVirtualDesktops Desktops, Launching Launching)
{
    /// <summary>The real Windows machine.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static DesktopNative Open() => new(
        new NativeWin32Windows(),
        new NativeWin32Actions(),
        ComVirtualDesktops.Open(),
        new Launching());
}

/// <summary>What <c>rig desktop</c> reads: the plan's location, this machine's aliases, the flags and the console.</summary>
/// <param name="Environment">Reads an environment variable.</param>
/// <param name="Home">The user's home folder.</param>
/// <param name="Checkout">The rig checkout, holding the machine plan template.</param>
/// <param name="AppData">The per-user state folder holding the restore and guard locks.</param>
/// <param name="Time">The clock restore's log files are stamped with.</param>
/// <param name="Output">Where every line goes.</param>
/// <param name="Error">Where failures go.</param>
/// <param name="Flags">The rig flags, read when a plan loads.</param>
/// <param name="Windows">Whether this machine has the native pieces; null elsewhere.</param>
internal sealed record DesktopHost(
    Func<string, string?> Environment,
    string Home,
    string Checkout,
    string AppData,
    TimeProvider Time,
    TextWriter Output,
    TextWriter Error,
    Func<IReadOnlyList<PlanFlag>> Flags,
    Func<DesktopNative?> Windows)
{
    /// <summary>The plan a command runs against when <c>--config</c> is not given.</summary>
    public string DefaultPlanPath() => PlanLoading.DefaultPath(Environment, Home, OperatingSystem.IsWindows());

    /// <summary>The logs folder beside a plan.</summary>
    public string LogsDirectory(string planPath) => PlanLoading.LogsDirectory(planPath, Environment);

    /// <summary>This machine's <c>tools.desktop.aliases</c>.</summary>
    public IReadOnlyDictionary<string, string> MachineAliases() =>
        MachineConfig.DesktopAliases(Path.Join(Home, ".workspace", "config.json"));

    /// <summary>The plan's placeholders resolved from the environment, the plan and this machine.</summary>
    public IReadOnlyDictionary<string, string> Substitutions(WorkspacePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Substitutions(Placeholders.EnvironmentAliases(Environment), MachineAliases());
    }

    /// <summary>The plan's flags with the stored overrides.</summary>
    public IReadOnlyList<PlanFlag> PlanFlags() => Flags();
}

/// <summary>
/// <c>rig desktop</c>: the desktop-manager commands, mounted on rig as the one CLI root (plan G5).
/// </summary>
/// <param name="host">Where it looks and writes.</param>
internal sealed class DesktopCommand(DesktopHost host)
{
    /// <summary>The group's name.</summary>
    public const string Name = "desktop";

    /// <summary>What <c>rig desktop</c> is.</summary>
    public const string Description =
        "Windows desktop workspaces: restore or audit a plan, align open windows, and guard them across monitor drop-outs.";

    /// <summary>The <c>desktop</c> command with its workspace subcommands.</summary>
    public Command Build()
    {
        var command = new Command(Name, Description);
        command.Subcommands.Add(new DesktopWorkspaceCommand(host).Build());
        return command;
    }

    /// <summary>The rig flags shaped as plan flags.</summary>
    public static IReadOnlyList<PlanFlag> PlanFlagsFrom(IReadOnlyList<FlagState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        return [.. states.Select(state => new PlanFlag(state.Flag.Name, state.Flag.Default, state.Override))];
    }
}
