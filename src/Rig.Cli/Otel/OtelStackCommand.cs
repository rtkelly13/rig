using System.CommandLine;
using System.Globalization;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Services;

namespace Rig.Cli.Otel;

/// <summary>What <c>otel-stack</c> needs from the machine, beyond the parts it drives.</summary>
/// <param name="Paths">Where the stack's files live.</param>
/// <param name="PlatformKey">This machine's pin key, e.g. <c>darwin_arm64</c>; throws <see cref="UnsupportedPlatformException"/>.</param>
/// <param name="MacOs">Whether this is macOS, where a LaunchAgent keeps the collector running.</param>
/// <param name="CheckoutTraces">This rig checkout's <c>temp/traces/sdk</c>, one of the roots backfill reads.</param>
/// <param name="Environment">Reads an environment variable, or returns null.</param>
/// <param name="Clock">Now, for pruning and backfill's settle window.</param>
/// <param name="Zone">The zone <c>collector status</c> shows times in.</param>
internal sealed record OtelHost(
    OtelPaths Paths, Func<string> PlatformKey, bool MacOs, string CheckoutTraces, Func<string, string?> Environment, TimeProvider Clock, TimeZoneInfo Zone);

/// <summary>The parts <c>otel-stack</c> drives; the composition root builds the real ones, tests fake them.</summary>
/// <param name="Runtime">Starts, stops and inspects the core collector.</param>
/// <param name="Probe">Whether a port answers.</param>
/// <param name="Installer">Downloads and verifies the pinned binaries.</param>
/// <param name="Launchd">Built only when a command needs launchd (macOS), since it looks up the user id.</param>
/// <param name="Pair">ClickHouse and the contrib collector.</param>
/// <param name="ClickHouse">The on-demand ClickHouse.</param>
/// <param name="Sender">Posts OTLP to the contrib collector.</param>
/// <param name="Sleep">Waits between backfill's confirmation checks.</param>
internal sealed record OtelParts(
    CollectorRuntime Runtime,
    IPortProbe Probe,
    OtelInstaller Installer,
    Func<CancellationToken, Task<Launchd>> Launchd,
    ClickHousePair Pair,
    ClickHouseClient ClickHouse,
    IOtlpSender Sender,
    Func<TimeSpan, CancellationToken, Task> Sleep);

/// <summary>
/// <c>otel-stack</c>: rig's local OpenTelemetry. Ported from <c>rig.otel_stack.cli</c> and <c>cli_clickhouse</c>. The
/// always-on core collector writes rotated files (7 days); <c>clickhouse</c> is the on-demand half, started by
/// <c>up</c> and stopped by <c>down</c>, and nothing in the always-on half depends on it. The collector commands
/// report everything on stdout, the ClickHouse ones their failures on stderr, as the Python did.
/// </summary>
internal sealed partial class OtelStackCommand(OtelHost host, OtelParts parts, TextWriter output, TextWriter error)
{
    /// <summary>A command that could not do its work exits with this.</summary>
    public const int FailedExit = 1;

    /// <summary><c>forward set</c> with something that is not an endpoint exits with this.</summary>
    public const int UsageExit = 2;

    /// <summary><c>ensure</c> and <c>start</c> exit with this when another process holds the collector's port.</summary>
    public const int ForeignListenerExit = 3;

    private const int Port = CollectorConfig.HttpPort;

    private static readonly string CollectorUrl = $"http://{ClickHouseConfig.Host}:{Text(ClickHouseConfig.CollectorPort)}";

    /// <summary>The <c>otel-stack</c> command.</summary>
    public Command Build() =>
        new("otel-stack", "Rig's local OpenTelemetry: an always-on collector writing rotated files (7 days), and ClickHouse on demand.")
        {
            CollectorCommand(),
            ClickHouseCommand(),
        };

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Mb(double bytes) => (bytes / 1_048_576).ToString("0.0", CultureInfo.InvariantCulture);

    private static Command Leaf(string name, string description, Func<CancellationToken, Task<int>> run)
    {
        var command = new Command(name, description);
        command.SetAction((_, ct) => run(ct));
        return command;
    }

    private string Key() => host.PlatformKey();

    private Command CollectorCommand()
    {
        var show = new Option<bool>("--print") { Description = "Also print it" };
        var config = new Command("config", "Write the collector config.") { show };
        config.SetAction(parsed =>
        {
            var path = parts.Runtime.WriteConfig();
            output.WriteLine($"wrote {path}");
            if (parsed.GetValue(show))
            {
                output.Write(File.ReadAllText(path));
            }

            return 0;
        });

        return new Command("collector", "The always-on core collector.")
        {
            Leaf("install", "Download the pinned otelcol, verify its sha256, unpack the binary. Idempotent.", InstallAsync),
            config,
            Leaf("start", "Start the collector now (via its LaunchAgent when one is loaded).", StartAsync),
            Leaf("stop", "Stop it. On macOS this unloads the agent until `ensure` or `service install` loads it again.", StopAsync),
            Leaf("ensure", "Install if missing, prune rotated files past 7 days, and make sure it is listening. Safe to run every tick.", EnsureAsync),
            Leaf("status", "Install state, ports, process memory, and what the data folder holds.", StatusAsync),
            ServiceCommand(),
            ForwardCommand(),
        };
    }

    private Command ServiceCommand() =>
        new("service", "The macOS LaunchAgent that keeps it running.")
        {
            Leaf("install", "Install and load the LaunchAgent (macOS). Installs the binary first if needed.", ServiceInstallAsync),
            Leaf("uninstall", "Unload and remove the LaunchAgent.", ServiceUninstallAsync),
        };

    private Command ForwardCommand()
    {
        var endpoint = new Argument<string>("endpoint") { Description = "OTLP/HTTP base URL, e.g. http://127.0.0.1:24318" };
        var set = new Command("set", "Also send everything to ENDPOINT (a Docker collector, say). The files stay the record.") { endpoint };
        set.SetAction(parsed =>
        {
            string recorded;
            try
            {
                recorded = parts.Runtime.SetForward(parsed.GetRequiredValue(endpoint))!;
            }
            catch (FormatException e)
            {
                output.WriteLine(e.Message);
                return UsageExit;
            }

            output.WriteLine($"forwarding to {recorded}; restart the collector to apply (`collector stop`, then `collector start`)");
            return 0;
        });

        var off = new Command("off", "Stop forwarding; only the files are written.");
        off.SetAction(_ =>
        {
            parts.Runtime.SetForward(null);
            output.WriteLine("forwarding off; restart the collector to apply (`collector stop`, then `collector start`)");
            return 0;
        });

        var show = new Command("show", "Print the forward endpoint, or `off`.");
        show.SetAction(_ =>
        {
            output.WriteLine(parts.Runtime.Forward() ?? "off");
            return 0;
        });

        return new Command("forward", "Optionally also send everything to another OTLP/HTTP collector, such as a Docker one.") { set, off, show };
    }

    private async Task<int> InstallAsync(CancellationToken ct)
    {
        var path = await parts.Installer.InstallCollectorAsync(host.Paths.Home, Key(), ct).ConfigureAwait(false);
        await output.WriteLineAsync($"otelcol {OtelPins.Version} verified at {path}").ConfigureAwait(false);
        return 0;
    }

    // True when something other than the core collector holds the HTTP port, having said so; false when it is ours or unknown.
    private async Task<bool> ForeignListenerAsync(CancellationToken ct)
    {
        if (await parts.Runtime.OwnerAsync(Port, ct).ConfigureAwait(false) is not { Ours: false } holder)
        {
            return false;
        }

        var name = holder.Command.Length > 0 ? holder.Command : "an unknown process";
        await output.WriteLineAsync(
            $"port {Text(Port)} is held by {name} (pid {Text(holder.Pid)}), not otelcol: free the port (for a Docker collector, publish it on "
            + "another port and `otel-stack collector forward set` to it), then run this again").ConfigureAwait(false);
        return true;
    }

    private async Task<bool> ServiceLoadedAsync(CancellationToken ct) =>
        host.MacOs && await OtelService.LoadedAsync(await parts.Launchd(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    private async Task<int> StartAsync(CancellationToken ct)
    {
        if (await parts.Probe.ListeningAsync(Port, cancellationToken: ct).ConfigureAwait(false))
        {
            if (await ForeignListenerAsync(ct).ConfigureAwait(false))
            {
                return ForeignListenerExit;
            }

            await output.WriteLineAsync($"already listening on {Text(Port)}").ConfigureAwait(false);
            return 0;
        }

        if (await ServiceLoadedAsync(ct).ConfigureAwait(false))
        {
            _ = parts.Runtime.WriteConfig();
            await (await parts.Launchd(ct).ConfigureAwait(false)).KickstartAsync(OtelService.Label, restart: true, ct).ConfigureAwait(false);
            await output.WriteLineAsync("kicked the LaunchAgent").ConfigureAwait(false);
            return 0;
        }

        var key = Key();
        if (!OtelInstaller.CollectorVerified(host.Paths.Home, key))
        {
            await output.WriteLineAsync("not installed: run `otel-stack collector install`").ConfigureAwait(false);
            return FailedExit;
        }

        var pid = await parts.Runtime.StartAsync(OtelPins.BinaryPath(host.Paths.Home, key), ct).ConfigureAwait(false);
        await output.WriteLineAsync($"started pid {Text(pid)}; log {host.Paths.Log}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> StopAsync(CancellationToken ct)
    {
        if (await ServiceLoadedAsync(ct).ConfigureAwait(false))
        {
            var launchd = await parts.Launchd(ct).ConfigureAwait(false);
            await launchd.UnloadAsync(OtelService.PlistPath(launchd), ct).ConfigureAwait(false);
            await output.WriteLineAsync("unloaded the LaunchAgent").ConfigureAwait(false);
            return 0;
        }

        var stopped = await parts.Runtime.StopRecordedAsync(ct).ConfigureAwait(false);
        await output.WriteLineAsync(stopped ? "stopped" : "no collector started by `otel-stack` is recorded").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> EnsureAsync(CancellationToken ct)
    {
        var binary = await parts.Installer.InstallCollectorAsync(host.Paths.Home, Key(), ct).ConfigureAwait(false);
        var config = parts.Runtime.WriteConfig();
        var pruned = Retention.Prune(host.Paths.Data, host.Clock.GetUtcNow(), apply: true);
        if (pruned.Count > 0)
        {
            await output.WriteLineAsync($"pruned {Text(pruned.Count)} rotated files older than {Text(Retention.KeepDays)} days").ConfigureAwait(false);
        }

        if (host.MacOs && OtelService.Installed(await parts.Launchd(ct).ConfigureAwait(false)))
        {
            var launchd = await parts.Launchd(ct).ConfigureAwait(false);
            if (!await OtelService.LoadedAsync(launchd, ct).ConfigureAwait(false))
            {
                _ = await OtelService.InstallAsync(launchd, host.Paths, binary, config, ct).ConfigureAwait(false);
                await output.WriteLineAsync("loaded the LaunchAgent").ConfigureAwait(false);
                return 0;
            }

            if (!await parts.Probe.ListeningAsync(Port, cancellationToken: ct).ConfigureAwait(false))
            {
                await launchd.KickstartAsync(OtelService.Label, restart: true, ct).ConfigureAwait(false);
                await output.WriteLineAsync("restarted the LaunchAgent").ConfigureAwait(false);
                return 0;
            }

            return await ListeningAsync(ct).ConfigureAwait(false);
        }

        if (await parts.Probe.ListeningAsync(Port, cancellationToken: ct).ConfigureAwait(false))
        {
            return await ListeningAsync(ct).ConfigureAwait(false);
        }

        var pid = await parts.Runtime.StartAsync(binary, ct).ConfigureAwait(false);
        await output.WriteLineAsync($"started pid {Text(pid)}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ListeningAsync(CancellationToken ct)
    {
        if (await ForeignListenerAsync(ct).ConfigureAwait(false))
        {
            return ForeignListenerExit;
        }

        await output.WriteLineAsync($"listening on {Text(Port)}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        var key = Key();
        var binary = OtelPins.BinaryPath(host.Paths.Home, key);
        var installed = File.Exists(binary) ? "installed" : "missing";
        var verified = OtelInstaller.CollectorVerified(host.Paths.Home, key) ? "sha256 verified" : "not verified";
        await output.WriteLineAsync($"binary: {installed} otelcol {OtelPins.Version} ({verified}) {binary}").ConfigureAwait(false);
        if (host.MacOs)
        {
            var launchd = await parts.Launchd(ct).ConfigureAwait(false);
            var state = await OtelService.LoadedAsync(launchd, ct).ConfigureAwait(false)
                ? "loaded"
                : OtelService.Installed(launchd) ? "installed, not loaded" : "not installed";
            await output.WriteLineAsync($"service: {state} ({OtelService.PlistPath(launchd)})").ConfigureAwait(false);
        }

        foreach (var port in new[] { CollectorConfig.GrpcPort, CollectorConfig.HttpPort })
        {
            await output.WriteLineAsync($"port {Text(port)}: {await PortStateAsync(port, ct).ConfigureAwait(false)}").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"forward: {parts.Runtime.Forward() ?? "off"}").ConfigureAwait(false);
        var holder = await parts.Runtime.OwnerAsync(Port, ct).ConfigureAwait(false);
        if ((holder is null || holder.Ours) && await parts.Runtime.ListenerPidAsync(Port, ct).ConfigureAwait(false) is { } pid)
        {
            var mb = await parts.Runtime.ResidentMbAsync(pid, ct).ConfigureAwait(false);
            var memory = mb is { } resident ? $", {resident.ToString("0.0", CultureInfo.InvariantCulture)} MB resident" : string.Empty;
            await output.WriteLineAsync($"process: pid {Text(pid)}{memory}").ConfigureAwait(false);
        }

        var data = host.Paths.Data;
        var files = Directory.Exists(data) ? Directory.GetFiles(data, "*.jsonl").Select(path => new FileInfo(path)).ToList() : [];
        if (files.Count == 0)
        {
            await output.WriteLineAsync($"data: empty ({data})").ConfigureAwait(false);
            return 0;
        }

        var stamps = files.Select(file => file.LastWriteTimeUtc).Order().ToList();
        await output.WriteLineAsync(
            $"data: {Text(files.Count)} files, {Mb(files.Sum(file => file.Length))} MB, {Local(stamps[0])} to {Local(stamps[^1])} ({data})").ConfigureAwait(false);
        return 0;
    }

    private string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc, host.Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private async Task<string> PortStateAsync(int port, CancellationToken ct)
    {
        if (!await parts.Probe.ListeningAsync(port, cancellationToken: ct).ConfigureAwait(false))
        {
            return "closed";
        }

        return await parts.Runtime.OwnerAsync(port, ct).ConfigureAwait(false) switch
        {
            null => "listening",
            { Ours: true } ours => $"listening (otelcol, pid {Text(ours.Pid)})",
            var other => $"held by {(other.Command.Length > 0 ? other.Command : "unknown process")} (pid {Text(other.Pid)}), not otelcol",
        };
    }

    private async Task<int> ServiceInstallAsync(CancellationToken ct)
    {
        if (!host.MacOs)
        {
            await output.WriteLineAsync("Only on macOS; on Windows the jobs coordinator runs `collector ensure`.").ConfigureAwait(false);
            return FailedExit;
        }

        var binary = await parts.Installer.InstallCollectorAsync(host.Paths.Home, Key(), ct).ConfigureAwait(false);
        var path = await OtelService.InstallAsync(await parts.Launchd(ct).ConfigureAwait(false), host.Paths, binary, parts.Runtime.WriteConfig(), ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync($"loaded {path}").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ServiceUninstallAsync(CancellationToken ct)
    {
        var removed = host.MacOs && await OtelService.UninstallAsync(await parts.Launchd(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        await output.WriteLineAsync(removed ? "removed" : "Not installed.").ConfigureAwait(false);
        return 0;
    }
}
