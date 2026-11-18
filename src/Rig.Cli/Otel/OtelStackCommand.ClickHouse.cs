using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using DotMake.CommandLine;
using Rig.Cli.Rendering;
using Rig.Core.Otel;

namespace Rig.Cli.Otel;

/// <summary><c>otel-stack clickhouse</c>: the on-demand half. Nothing here runs until <c>up</c>, and <c>down</c> leaves nothing running.</summary>
internal sealed partial class OtelStackCommand
{
    private Command ClickHouseGroup()
    {
        var clickHouse = DeclarativeCommand.BuildGroup(new ClickHouseCommand());
        clickHouse.Add(DeclarativeCommand.Build(new ClickHouseInstallCommand(this)));
        clickHouse.Add(DeclarativeCommand.Build(new UpCommand(this)));
        clickHouse.Add(DeclarativeCommand.Build(new DownCommand(this)));
        clickHouse.Add(DeclarativeCommand.Build(new ClickHouseStatusCommand(this)));
        clickHouse.Add(DeclarativeCommand.Build(new QueryCommand(this)));
        clickHouse.Add(DeclarativeCommand.Build(new BackfillCommand(this)));
        return clickHouse;
    }

    [GeneratedRegex(@"\bFORMAT\s+\w+\s*;?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatClause();

    // Each pin's verified binary, installing what is missing; ClickHouse first.
    private async Task<IReadOnlyList<string>> InstallPinsAsync(CancellationToken ct)
    {
        var key = Key();
        List<string> binaries = [];
        foreach (var pin in new[] { OtelPins.ClickHousePin(key), OtelPins.ContribPin(key) })
        {
            binaries.Add(await parts.Installer.InstallAsync(pin, host.Paths.Home, ct).ConfigureAwait(false));
        }

        return binaries;
    }

    private async Task<int> ClickHouseInstallAsync(CancellationToken ct)
    {
        IReadOnlyList<string> binaries;
        try
        {
            binaries = await InstallPinsAsync(ct).ConfigureAwait(false);
        }
        catch (UnsupportedPlatformException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return FailedExit;
        }

        foreach (var binary in binaries)
        {
            await output.WriteLineAsync($"verified {binary}").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> UpAsync(CancellationToken ct)
    {
        UpResult result;
        try
        {
            var binaries = await InstallPinsAsync(ct).ConfigureAwait(false);
            result = await parts.Pair.UpAsync(binaries[0], binaries[1], parts.ClickHouse.PingAsync, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is UnsupportedPlatformException or StartupException)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return FailedExit;
        }

        foreach (var name in result.Started)
        {
            await output.WriteLineAsync($"started {name}").ConfigureAwait(false);
        }

        foreach (var name in result.Already)
        {
            await output.WriteLineAsync($"already running: {name}").ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"ClickHouse http://{ClickHouseConfig.Host}:{Text(ClickHouseConfig.HttpPort)} (database {ClickHouseConfig.Database}); "
            + $"OTLP for backfill {CollectorUrl}. `otel-stack clickhouse down` stops both.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> DownAsync(CancellationToken ct)
    {
        var stopped = await parts.Pair.DownAsync(ct).ConfigureAwait(false);
        await output.WriteLineAsync(stopped.Count > 0 ? $"stopped: {string.Join(", ", stopped)}" : "nothing was running").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> ClickHouseStatusAsync(CancellationToken ct)
    {
        foreach (var part in await parts.Pair.StatusAsync(ct).ConfigureAwait(false))
        {
            if (part.Pid is not { } pid)
            {
                await output.WriteLineAsync($"{part.Name}: not running").ConfigureAwait(false);
                continue;
            }

            var memory = part.ResidentMb is { } mb ? $", {mb.ToString("0.0", CultureInfo.InvariantCulture)} MB resident" : string.Empty;
            await output.WriteLineAsync($"{part.Name}: pid {Text(pid)}{memory}").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"data: {Mb(parts.Pair.DataBytes())} MB in {host.Paths.ClickHouseData}").ConfigureAwait(false);
        if (!await parts.ClickHouse.PingAsync(ct).ConfigureAwait(false))
        {
            return 0;
        }

        var table = new TextTable("tables", [("table", false), ("rows", true), ("on disk", true)]);
        foreach (var stats in await parts.ClickHouse.TableStatsAsync(ct).ConfigureAwait(false))
        {
            table.Add(stats.Table, stats.Rows.ToString(CultureInfo.InvariantCulture), $"{Mb(stats.BytesOnDisk)} MB");
        }

        await output.WriteAsync(table.Render()).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> QueryAsync(string sql, string format, CancellationToken ct)
    {
        var statement = sql.Trim().TrimEnd(';');
        if (!FormatClause().IsMatch(statement))
        {
            statement = $"{statement}\nFORMAT {format}";
        }

        try
        {
            await output.WriteAsync(await parts.ClickHouse.TextAsync(statement, cancellationToken: ct).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (ClickHouseException e)
        {
            await error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return FailedExit;
        }

        return 0;
    }

    private async Task<int> BackfillAsync(IReadOnlyList<string> extra, int settleSeconds, bool dryRun, CancellationToken ct)
    {
        if (!await parts.ClickHouse.PingAsync(ct).ConfigureAwait(false))
        {
            await error.WriteLineAsync("ClickHouse is not answering; run `otel-stack clickhouse up` first. Nothing sent.").ConfigureAwait(false);
            return FailedExit;
        }

        var roots = Backfill.DefaultRoots(host.Paths.BaseDir, host.CheckoutTraces, host.Environment(Backfill.TraceDirEnv));
        var files = Backfill.Discover(roots, [.. Backfill.CollectorTraceFiles(host.Paths.Data), .. extra]);
        var report = await new Backfill(parts.ClickHouse, parts.Sender, host.Clock, parts.Sleep)
            .RunAsync(files, Path.Join(host.Paths.ClickHouseHome, Backfill.StateFile), settleSeconds, dryRun, ct)
            .ConfigureAwait(false);
        var replayed = await new Replay(parts.Sender)
            .RunAsync(host.Paths.Data, Path.Join(host.Paths.ClickHouseHome, ReplayOffsets.StateFile), dryRun, ct)
            .ConfigureAwait(false);
        var verb = dryRun ? "would send" : "sent";
        await output.WriteLineAsync(
            $"traces: {Text(files.Count)} files, {Text(report.Unchanged)} unchanged since done, {Text(report.Files.Count)} checked, "
            + $"{Text(report.Sent)} spans {verb}").ConfigureAwait(false);
        await output.WriteLineAsync(
            $"logs and metrics: {Text(replayed.Sum(result => result.Lines))} lines {verb} from {Text(replayed.Count)} files").ConfigureAwait(false);
        string[] problems = [.. report.Errors, .. replayed.Where(result => result.Error.Length > 0).Select(result => result.Error)];
        if (problems.Length == 0)
        {
            return 0;
        }

        await error.WriteLineAsync(string.Join("\n", problems)).ConfigureAwait(false);
        return FailedExit;
    }

    /// <summary><c>otel-stack clickhouse</c>.</summary>
    [CliCommand(Name = "clickhouse", Description = "ClickHouse on demand: `up`, `backfill` the recorded files, `query`, then `down`. Tables keep 7 days.")]
    internal sealed class ClickHouseCommand
    {
    }

    /// <summary><c>otel-stack clickhouse install</c>.</summary>
    [CliCommand(Name = "install", Description = "Download and verify the pinned ClickHouse and otelcol-contrib, without starting anything.")]
    internal sealed class ClickHouseInstallCommand(OtelStackCommand stack)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal ClickHouseInstallCommand()
            : this(null!)
        {
        }

        /// <summary>The handler.</summary>
        public Task<int> RunAsync(CliContext context) => stack.ClickHouseInstallAsync(context.CancellationToken);
    }

    /// <summary><c>otel-stack clickhouse up</c>.</summary>
    [CliCommand(Name = "up", Description = "Install what is missing, start ClickHouse and the contrib collector, wait until both answer.")]
    internal sealed class UpCommand(OtelStackCommand stack)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal UpCommand()
            : this(null!)
        {
        }

        /// <summary>The handler.</summary>
        public Task<int> RunAsync(CliContext context) => stack.UpAsync(context.CancellationToken);
    }

    /// <summary><c>otel-stack clickhouse down</c>.</summary>
    [CliCommand(Name = "down", Description = "Stop the contrib collector and ClickHouse, and wait for both to exit. The data stays.")]
    internal sealed class DownCommand(OtelStackCommand stack)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal DownCommand()
            : this(null!)
        {
        }

        /// <summary>The handler.</summary>
        public Task<int> RunAsync(CliContext context) => stack.DownAsync(context.CancellationToken);
    }

    /// <summary><c>otel-stack clickhouse status</c>.</summary>
    [CliCommand(Name = "status", Description = "Whether each part runs, its memory, and what the data folder and tables hold.")]
    internal sealed class ClickHouseStatusCommand(OtelStackCommand stack)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal ClickHouseStatusCommand()
            : this(null!)
        {
        }

        /// <summary>The handler.</summary>
        public Task<int> RunAsync(CliContext context) => stack.ClickHouseStatusAsync(context.CancellationToken);
    }

    /// <summary><c>otel-stack clickhouse query</c>.</summary>
    [CliCommand(Name = "query", Description = "Run SQL against the on-demand ClickHouse and print the result.")]
    internal sealed class QueryCommand(OtelStackCommand stack)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal QueryCommand()
            : this(null!)
        {
        }

        /// <summary>The SQL to run.</summary>
        [CliArgument(Name = "sql", Description = "SQL to run, e.g. \"SELECT count() FROM otel.otel_traces\".")]
        public string Sql { get; set; } = null!;

        /// <summary>The output format.</summary>
        [CliOption(Name = "--format", Description = "ClickHouse output format, unless the SQL names one.")]
        public string Format { get; set; } = "PrettyCompactMonoBlock";

        /// <summary>Runs the query.</summary>
        public Task<int> RunAsync(CliContext context) => stack.QueryAsync(Sql, Format, context.CancellationToken);
    }

    /// <summary><c>otel-stack clickhouse backfill</c>.</summary>
    [CliCommand(Name = "backfill", Description = "Load the recorded files into ClickHouse: spans by identity, logs and metrics by position. Run after `up`.")]
    internal sealed class BackfillCommand(OtelStackCommand stack)
    {
        /// <summary>The instance DotMake binds onto; rig always builds the wired one above instead.</summary>
        [ExcludeFromCodeCoverage(Justification = "DotMake binds onto this instance; rig builds the wired one.")]
        internal BackfillCommand()
            : this(null!)
        {
        }

        /// <summary>Extra NDJSON files to replay.</summary>
        [CliOption(Name = "--path", Required = false, Description = "Also replay this NDJSON trace file (repeatable).")]
        public FileInfo[] Paths { get; set; } = [];

        /// <summary>Seconds a span must have ended before it is sent.</summary>
        [CliOption(Name = "--settle-seconds", Description = "Leave spans that ended this recently for the next run.")]
        public int SettleSeconds { get; set; } = Backfill.SettleSeconds;

        /// <summary>Counts without sending.</summary>
        [CliOption(Name = "--dry-run", Description = "Count what would be sent; send and mark nothing.")]
        public bool DryRun { get; set; }

        /// <summary>Loads the files.</summary>
        public Task<int> RunAsync(CliContext context) =>
            stack.BackfillAsync([.. Paths.Select(file => file.FullName)], SettleSeconds, DryRun, context.CancellationToken);
    }
}
