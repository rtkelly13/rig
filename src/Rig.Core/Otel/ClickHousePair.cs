using System.Globalization;
using System.Text;

namespace Rig.Core.Otel;

/// <summary>A process was started but did not come up in time; the log says why.</summary>
public sealed class StartupException : Exception
{
    /// <summary>Creates the exception.</summary>
    public StartupException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public StartupException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and its cause.</summary>
    public StartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One of the two processes <see cref="ClickHousePair.UpAsync"/> starts.</summary>
/// <param name="Name">What it is called in output.</param>
/// <param name="PidFile">Where its pid is recorded.</param>
/// <param name="Log">Where its output is appended.</param>
public sealed record StackPart(string Name, string PidFile, string Log);

/// <summary>The config files <see cref="ClickHousePair.WriteConfigs"/> writes, regenerated on every start.</summary>
/// <param name="Server">ClickHouse's server config.</param>
/// <param name="Users">Its users config.</param>
/// <param name="Collector">The contrib collector's config.</param>
public sealed record StackConfigs(string Server, string Users, string Collector);

/// <summary>What <see cref="ClickHousePair.UpAsync"/> started, and what was already running.</summary>
/// <param name="Started">The parts it started.</param>
/// <param name="Already">The parts already running.</param>
public sealed record UpResult(IReadOnlyList<string> Started, IReadOnlyList<string> Already);

/// <summary>Whether a part runs, and its memory.</summary>
/// <param name="Name">The part.</param>
/// <param name="Pid">Its pid, or null when it is not running.</param>
/// <param name="ResidentMb">Its resident memory, where that can be read.</param>
public sealed record PartStatus(string Name, int? Pid, double? ResidentMb);

/// <summary>
/// The on-demand pair: ClickHouse, and the contrib collector that writes into it; ported from
/// <c>rig.otel_stack.clickhouse</c>. Nothing is scheduled. <see cref="UpAsync"/> installs (pinned, verified) what is
/// missing, writes the configs, starts ClickHouse and waits for it to answer, then starts the collector and waits for
/// its port. <see cref="DownAsync"/> stops both and waits until they have exited. The always-on core collector is
/// untouched by either. A pid file whose process has gone is treated as not running and removed.
/// </summary>
/// <param name="paths">Where the stack's files live.</param>
/// <param name="signals">Whether a pid exists, and stopping it.</param>
/// <param name="detached">Starts the two processes.</param>
/// <param name="probe">Whether the collector's port answers.</param>
/// <param name="runtime">Reads a process's resident memory.</param>
/// <param name="time">Measures the waits.</param>
/// <param name="sleep">Pauses between checks; tests advance <paramref name="time"/> instead.</param>
public sealed class ClickHousePair(
    OtelPaths paths,
    IProcessSignals signals,
    IDetachedStart detached,
    IPortProbe probe,
    CollectorRuntime runtime,
    TimeProvider time,
    Func<TimeSpan, CancellationToken, Task> sleep)
{
    /// <summary>How long <see cref="UpAsync"/> waits for each part to answer.</summary>
    public static readonly TimeSpan UpWait = TimeSpan.FromSeconds(60);

    /// <summary>How long <see cref="DownAsync"/> waits for each part to exit before SIGKILL.</summary>
    public static readonly TimeSpan DownWait = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(0.5);

    // ClickHouse otherwise forks itself under a watchdog parent, so the recorded pid would be the watchdog's, its
    // memory a misleading 2 MB, and there would be one more process awake.
    private static readonly Dictionary<string, string> ServerEnvironment = new(StringComparer.Ordinal) { ["CLICKHOUSE_WATCHDOG_ENABLE"] = "0" };

    /// <summary>ClickHouse itself.</summary>
    public StackPart Server => new("clickhouse", Path.Join(paths.ClickHouseHome, "clickhouse.pid"), paths.ClickHouseLog);

    /// <summary>The contrib collector that writes into it.</summary>
    public StackPart Collector => new("otelcol-contrib", Path.Join(paths.ClickHouseHome, "otelcol-contrib.pid"), paths.ContribLog);

    /// <summary>Both parts, in start order.</summary>
    public IReadOnlyList<StackPart> Parts => [Server, Collector];

    /// <summary>Writes the server, users and collector configs beside the data folder, creating it.</summary>
    public StackConfigs WriteConfigs()
    {
        var home = paths.ClickHouseHome;
        _ = Directory.CreateDirectory(paths.ClickHouseData);
        var written = new StackConfigs(Path.Join(home, "server.xml"), Path.Join(home, "users.xml"), Path.Join(home, "otelcol-contrib.yaml"));
        var utf8 = new UTF8Encoding(false);
        File.WriteAllText(written.Server, ClickHouseConfig.ServerXml(paths.ClickHouseData, written.Users), utf8);
        File.WriteAllText(written.Users, ClickHouseConfig.UsersXml(), utf8);
        File.WriteAllText(written.Collector, ClickHouseConfig.CollectorYaml(), utf8);
        return written;
    }

    /// <summary>
    /// The recorded pid while it is still the process that was started: alive, and the same binary and start time
    /// (<see cref="CollectorRuntime.StillOursAsync"/>). The pid file outlives reboots, so a pid alone could name a later,
    /// unrelated process, which <see cref="DownAsync"/> would then signal. Anything else is stale and its file removed.
    /// </summary>
    public async Task<int?> RunningPidAsync(StackPart part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        string text;
        try
        {
            text = await File.ReadAllTextAsync(part.PidFile, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (Launched.Parse(text) is { Binary.Length: > 0 } record
            && signals.Alive(record.Pid)
            && await runtime.StillOursAsync(record, cancellationToken).ConfigureAwait(false))
        {
            return record.Pid;
        }

        File.Delete(part.PidFile);
        return null;
    }

    /// <summary>Starts ClickHouse (<paramref name="server"/>) and the contrib collector (<paramref name="contrib"/>) unless already running; waits for each.</summary>
    /// <param name="server">The installed ClickHouse binary.</param>
    /// <param name="contrib">The installed otelcol-contrib binary.</param>
    /// <param name="ping">Whether ClickHouse answers.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <exception cref="StartupException">A part exited during startup, or did not answer within <see cref="UpWait"/>.</exception>
    public async Task<UpResult> UpAsync(string server, string contrib, Func<CancellationToken, Task<bool>> ping, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ping);
        var configs = WriteConfigs();
        List<string> started = [];
        List<string> already = [];

        await StartUnlessRunningAsync(Server, [server, "server", $"--config-file={configs.Server}"], paths.ClickHouseData, ServerEnvironment, started, already, cancellationToken).ConfigureAwait(false);
        await WaitAsync(ping, Server, cancellationToken).ConfigureAwait(false);

        await StartUnlessRunningAsync(Collector, [contrib, "--config", configs.Collector], paths.ClickHouseHome, null, started, already, cancellationToken).ConfigureAwait(false);
        await WaitAsync(ct => probe.ListeningAsync(ClickHouseConfig.CollectorPort, cancellationToken: ct), Collector, cancellationToken).ConfigureAwait(false);
        return new UpResult(started, already);
    }

    /// <summary>Stops the collector, then ClickHouse, waiting for each to exit (SIGKILL after <see cref="DownWait"/>); returns what stopped.</summary>
    public async Task<IReadOnlyList<string>> DownAsync(CancellationToken cancellationToken = default)
    {
        List<string> stopped = [];
        foreach (var part in Parts.Reverse())
        {
            if (await RunningPidAsync(part, cancellationToken).ConfigureAwait(false) is not { } pid)
            {
                continue;
            }

            signals.Signal(pid);
            var begun = time.GetTimestamp();
            while (signals.Alive(pid) && time.GetElapsedTime(begun) < DownWait)
            {
                await sleep(Poll, cancellationToken).ConfigureAwait(false);
            }

            if (signals.Alive(pid))
            {
                signals.Signal(pid, kill: true);
            }

            File.Delete(part.PidFile);
            stopped.Add(part.Name);
        }

        return stopped;
    }

    /// <summary>Each part's pid and memory.</summary>
    public async Task<IReadOnlyList<PartStatus>> StatusAsync(CancellationToken cancellationToken = default)
    {
        List<PartStatus> statuses = [];
        foreach (var part in Parts)
        {
            var pid = await RunningPidAsync(part, cancellationToken).ConfigureAwait(false);
            var memory = pid is { } running ? await runtime.ResidentMbAsync(running, cancellationToken).ConfigureAwait(false) : null;
            statuses.Add(new PartStatus(part.Name, pid, memory));
        }

        return statuses;
    }

    /// <summary>The bytes of the regular files (not links) under the data folder.</summary>
    public long DataBytes()
    {
        var root = new DirectoryInfo(paths.ClickHouseData);
        if (!root.Exists)
        {
            return 0;
        }

        // Links are skipped and not followed, as Python's rglob did.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return root.EnumerateFiles("*", options).Sum(file => file.Length);
    }

    private async Task StartUnlessRunningAsync(
        StackPart part,
        IReadOnlyList<string> argv,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        List<string> started,
        List<string> already,
        CancellationToken cancellationToken)
    {
        if (await RunningPidAsync(part, cancellationToken).ConfigureAwait(false) is not null)
        {
            already.Add(part.Name);
            return;
        }

        _ = Directory.CreateDirectory(Path.GetDirectoryName(part.Log)!);
        var pid = await detached.StartAsync(argv, part.Log, workingDirectory, environment, cancellationToken).ConfigureAwait(false);
        var record = new Launched(pid, argv[0], await runtime.ProcessStartedAsync(pid, cancellationToken).ConfigureAwait(false));
        await File.WriteAllTextAsync(part.PidFile, record.Serialise(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        started.Add(part.Name);
    }

    private async Task WaitAsync(Func<CancellationToken, Task<bool>> ready, StackPart part, CancellationToken cancellationToken)
    {
        var begun = time.GetTimestamp();
        while (!await ready(cancellationToken).ConfigureAwait(false))
        {
            if (await RunningPidAsync(part, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new StartupException($"{part.Name} exited during startup; see {part.Log}");
            }

            if (time.GetElapsedTime(begun) >= UpWait)
            {
                throw new StartupException($"{part.Name} did not come up within {UpWait.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s; see {part.Log}");
            }

            await sleep(Poll, cancellationToken).ConfigureAwait(false);
        }
    }
}
