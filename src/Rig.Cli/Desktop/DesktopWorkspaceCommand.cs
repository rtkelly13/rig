#pragma warning disable RS0030 // Legacy System.CommandLine awaiting DotMake migration (Issue #234)
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using Rig.Core.Desktop;

namespace Rig.Cli.Desktop;

/// <summary>
/// <c>rig desktop workspace</c>: the layout manager and display guard.
/// </summary>
/// <param name="host">Where it looks and writes.</param>
[ExcludeFromCodeCoverage(Justification = "Desktop workspace commands require a live Windows desktop environment.")]
internal sealed partial class DesktopWorkspaceCommand(DesktopHost host)
{
    /// <summary>The group's name under <c>desktop</c>.</summary>
    public const string Name = "workspace";

    /// <summary>What the group is.</summary>
    public const string Description =
        "Windows workspace management: restore, audit and align a plan, name its desktops, and guard its windows across monitor drop-outs.";

    /// <summary>The exit code <c>align</c> uses when some window moves failed.</summary>
    public const int AlignMovesFailed = 3;

    /// <summary>The exit code <c>restore</c> uses when fewer screens are live than the plan places windows on.</summary>
    public const int RestoreDeferred = 4;

    /// <summary>The <c>workspace</c> group with every subcommand.</summary>
    public Command Build()
    {
        return new Command(Name, Description)
        {
            Restore(),
            Audit(),
            Status(),
            ValidatePlan(),
            Features(),
            ExportPlan(),
            WireMachine(),
            AlignCommand(),
            NameDesktops(),
            Guard(),
            DetectApp(),
            LaunchFile(),
            TestItem(),
            RestartApp(),
            UpdateOrca(),
        };
    }

    private static Option<string?> ConfigOption(string help) =>
        new("--config") { Description = help };

    private static Option<string[]> EnableOption(string help) =>
        new("--enable") { Description = help, DefaultValueFactory = _ => [], AllowMultipleArgumentsPerToken = false };

    private static Option<string[]> DisableOption(string help) =>
        new("--disable") { Description = help, DefaultValueFactory = _ => [], AllowMultipleArgumentsPerToken = false };

    private static Option<string?> DesktopOption(string help) => new("--desktop") { Description = help };

    private static Option<int?> ScreenOption(string help)
    {
        var option = new Option<int?>("--screen") { Description = help };
        option.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int?>() is < 1)
            {
                result.AddError("--screen must be at least 1.");
            }
        });
        return option;
    }

    private static Option<bool> DryRunOption(string help) => new("--dry-run") { Description = help };

    private string ConfigPath(string? config) => config ?? host.DefaultPlanPath();

    private void FeatureLine(WorkspacePlan plan)
    {
        if (plan.Features.Count == 0)
        {
            return;
        }

        var rendered = string.Join("  ", plan.Features.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={(pair.Value ? "on" : "off")}"));
        host.Output.WriteLine($"Features: {rendered}");
    }

    /// <summary>The plan a command runs against: flags applied and columns fitted, but not yet verified.</summary>
    private WorkspacePlan LoadContext(string path, IReadOnlyList<string> enable, IReadOnlyList<string> disable)
    {
        var screenWidths = host.Windows() is { } native
            ? native.Win32.Monitors().Select((m, i) => (Screen: i + 1, Width: m.Bounds.Width)).ToDictionary(x => x.Screen, x => x.Width)
            : null;
        return PlanLoading.Load(path, host.Flags, enable, disable, screenWidths: screenWidths, verify: false);
    }

    /// <summary>The plan a command runs against, verified as <c>load_plan</c> verifies it.</summary>
    private WorkspacePlan Load(string path, IReadOnlyList<string> enable, IReadOnlyList<string> disable)
    {
        var plan = LoadContext(path, enable, disable);
        return PlanLoading.Verify(plan, path, host.Substitutions(plan), new LocalPathProbe());
    }

    private sealed record Loaded(string Path, WorkspacePlan Plan);

    private sealed record Opened(DesktopNative Machine, string Path, WorkspacePlan Plan);

    /// <summary>Reads the plan a command runs against; prints why and returns null when it cannot.</summary>
    private async Task<Loaded?> ReadPlan(string? config, IReadOnlyList<string>? enable = null, IReadOnlyList<string>? disable = null, bool verify = true)
    {
        var path = ConfigPath(config);
        if (!File.Exists(path))
        {
            await host.Output.WriteLineAsync($"Config not found: {path}").ConfigureAwait(false);
            return null;
        }

        try
        {
            return new Loaded(path, verify ? Load(path, enable ?? [], disable ?? []) : LoadContext(path, enable ?? [], disable ?? []));
        }
        catch (Exception error) when (error is InvalidPlanException or UnknownFeatureException or IOException)
        {
            await host.Output.WriteLineAsync(error.Message).ConfigureAwait(false);
            return null;
        }
    }

    private async Task<DesktopNative?> WindowsOnly(string command)
    {
        if (host.Windows() is { } native && OperatingSystem.IsWindows())
        {
            return native;
        }

        await host.Output.WriteLineAsync($"{command} requires Windows.").ConfigureAwait(false);
        return null;
    }

    /// <summary>Reads the plan on a Windows machine; prints why and returns null when either is missing.</summary>
    private async Task<Opened?> Open(string command, string? config, IReadOnlyList<string>? enable = null, IReadOnlyList<string>? disable = null) =>
        await WindowsOnly(command).ConfigureAwait(false) is { } native && await ReadPlan(config, enable, disable).ConfigureAwait(false) is { } loaded
            ? new Opened(native, loaded.Path, loaded.Plan)
            : null;
}
