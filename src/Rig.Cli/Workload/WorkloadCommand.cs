using System.CommandLine;
using System.ComponentModel;
using System.Globalization;
using Rig.Core.Processes;
using Rig.Core.Services;
using Rig.Core.Workload;

namespace Rig.Cli.Workload;

/// <summary>What <c>workload</c> needs from the machine, beyond the parts it drives.</summary>
/// <param name="Paths">Where samples live.</param>
/// <param name="MacOs">Whether this is macOS, where a LaunchAgent schedules sampling.</param>
/// <param name="Os">The <c>os</c> samples record: <c>windows</c> or <c>macos</c>.</param>
/// <param name="Machine">The <c>host</c> samples record.</param>
/// <param name="Clock">Now.</param>
/// <param name="Zone">The zone summaries show times in.</param>
/// <param name="Find">Finds a program on PATH, or returns null; the agent's PATH is built from what it finds.</param>
internal sealed record WorkloadHost(
    WorkloadPaths Paths, bool MacOs, string Os, string Machine, TimeProvider Clock, TimeZoneInfo Zone, Func<string, string?> Find);

/// <summary>The parts <c>workload</c> drives; the composition root builds the real ones, tests fake them.</summary>
/// <param name="Table">Reads the process table.</param>
/// <param name="Launchd">Built only when a command needs launchd (macOS), since it looks up the user id.</param>
internal sealed record WorkloadParts(IProcessTable Table, Func<CancellationToken, Task<Launchd>> Launchd);

/// <summary>
/// <c>workload</c>: a cheap sample of what is using memory, every 15 minutes, kept for a week (docs/workload.md). The jobs
/// coordinator runs <c>sample</c> on Windows; a scheduled LaunchAgent runs it on macOS.
/// </summary>
internal sealed class WorkloadCommand(WorkloadHost host, WorkloadParts parts, TextWriter output, TextWriter error)
{
    /// <summary>A command that could not do its work exits with this.</summary>
    public const int FailedExit = 1;

    /// <summary>The window <c>summary</c> covers by default, in hours.</summary>
    public const int DefaultHours = 24;

    /// <summary>The <c>workload</c> command.</summary>
    public Command Build() =>
        new("workload", "Sample memory use by app every 15 minutes (7 days kept) and summarise it.")
        {
            Leaf("sample", "Take one sample, append it to today's file and prune files past 7 days. Safe to run every tick.", SampleAsync),
            Leaf("status", "What has been recorded, and on macOS whether the LaunchAgent is loaded.", StatusAsync),
            SummaryCommand(),
            ServiceCommand(),
        };

    private static string Gb(long mb) => (mb / 1024d).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static Command Leaf(string name, string description, Func<CancellationToken, Task<int>> run)
    {
        var command = new Command(name, description);
        command.SetAction((_, ct) => run(ct));
        return command;
    }

    private Command SummaryCommand()
    {
        var hours = new Option<int>("--hours") { Description = "How far back to look", DefaultValueFactory = _ => DefaultHours };
        var command = new Command("summary", "Read the samples back: free memory, the peak and who held it, Claude sessions, large processes, bursts.") { hours };
        command.SetAction(parsed =>
        {
            var window = parsed.GetValue(hours);
            if (window <= 0)
            {
                error.WriteLine("--hours must be at least 1");
                return FailedExit;
            }

            var samples = host.Paths.Read(host.Clock.GetUtcNow() - TimeSpan.FromHours(window));
            foreach (var line in WorkloadSummary.Lines(samples, host.Zone))
            {
                output.WriteLine(line);
            }

            return 0;
        });
        return command;
    }

    private Command ServiceCommand() =>
        new("service", "The macOS LaunchAgent that samples every 15 minutes.")
        {
            Leaf("install", "Install and load the LaunchAgent (macOS).", ServiceInstallAsync),
            Leaf("uninstall", "Unload and remove the LaunchAgent.", ServiceUninstallAsync),
            Leaf("status", "Whether the LaunchAgent is installed and loaded.", ServiceStatusAsync),
        };

    private async Task<int> SampleAsync(CancellationToken ct)
    {
        SampleResult result;
        try
        {
            result = await new WorkloadSampler(parts.Table, AppRules.Bundled, host.Paths, SampleLimits.Default)
                .RunAsync(host.Clock.GetUtcNow(), host.Machine, host.Os, ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or ProcException or IOException or UnauthorizedAccessException or FormatException)
        {
            await error.WriteLineAsync($"could not sample: {e.Message}").ConfigureAwait(false);
            return FailedExit;
        }

        var sample = result.Sample;
        var pruned = result.Pruned.Count == 0 ? string.Empty : Invariant($"; pruned {result.Pruned.Count} old file(s)");
        await output.WriteLineAsync(Invariant(
            $"{sample.Ts:yyyy-MM-ddTHH:mm:ssZ} free {Gb(sample.Ram.FreeMb)}/{Gb(sample.Ram.TotalMb)} GB, private {Gb(sample.Processes.PrivateMb)} GB in {sample.Processes.Count} processes, {sample.Claude.Sessions} Claude sessions, {sample.Large.Count} large, {sample.Bursts.Count} bursts -> {result.File} ({result.Bytes} bytes){pruned}"))
            .ConfigureAwait(false);
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        var files = host.Paths.DayFiles();
        await output.WriteLineAsync($"folder: {host.Paths.Folder}").ConfigureAwait(false);
        if (files.Count == 0)
        {
            await output.WriteLineAsync("no samples yet").ConfigureAwait(false);
        }
        else
        {
            var bytes = files.Sum(file => new FileInfo(file).Length);
            await output.WriteLineAsync(Invariant(
                $"{files.Count} day file(s), {Path.GetFileName(files[0])} to {Path.GetFileName(files[^1])}, {bytes / 1024d:0.0} KB")).ConfigureAwait(false);
            var latest = host.Paths.Read(host.Clock.GetUtcNow() - TimeSpan.FromDays(1));
            if (latest.Count > 0)
            {
                var newest = latest[^1];
                var age = host.Clock.GetUtcNow().UtcDateTime - newest.Ts;
                await output.WriteLineAsync(Invariant(
                    $"{latest.Count} sample(s) in the last 24 h; newest {TimeZoneInfo.ConvertTimeFromUtc(newest.Ts, host.Zone):yyyy-MM-dd HH:mm} ({age.TotalMinutes:0} min ago)"))
                    .ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync("no sample in the last 24 h").ConfigureAwait(false);
            }
        }

        if (host.MacOs)
        {
            await ServiceStatusAsync(ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> ServiceInstallAsync(CancellationToken ct)
    {
        if (!host.MacOs)
        {
            await error.WriteLineAsync("Only on macOS; on Windows the jobs coordinator runs `workload sample` (rig jobs install).").ConfigureAwait(false);
            return FailedExit;
        }

        if (host.Find("rig") is not { } rig)
        {
            await error.WriteLineAsync("rig is not on PATH; run `rig runtime install` first.").ConfigureAwait(false);
            return FailedExit;
        }

        var path = WorkloadService.PathFor(new[] { rig, host.Find("dotnet"), host.Find("uv") }.OfType<string>());
        var plist = await WorkloadService.InstallAsync(await parts.Launchd(ct).ConfigureAwait(false), host.Paths, rig, path, ct).ConfigureAwait(false);
        await output.WriteLineAsync($"loaded {plist}; samples every {WorkloadService.IntervalSeconds / 60} minutes into {host.Paths.Folder}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ServiceUninstallAsync(CancellationToken ct)
    {
        var removed = host.MacOs && await WorkloadService.UninstallAsync(await parts.Launchd(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync(removed ? "removed" : "Not installed.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ServiceStatusAsync(CancellationToken ct)
    {
        if (!host.MacOs)
        {
            await output.WriteLineAsync("service: macOS only; on Windows the jobs coordinator runs `workload sample`").ConfigureAwait(false);
            return 0;
        }

        var launchd = await parts.Launchd(ct).ConfigureAwait(false);
        var installed = WorkloadService.Installed(launchd);
        var loaded = installed && await WorkloadService.LoadedAsync(launchd, ct).ConfigureAwait(false);
        await output.WriteLineAsync($"service: {(loaded ? "loaded" : installed ? "installed, not loaded" : "not installed")} ({WorkloadService.PlistPath(launchd)})")
            .ConfigureAwait(false);
        return 0;
    }
}
