namespace Rig.Core.Otel;

/// <summary>
/// Where the collector's binary, config, data and log live: all under rig's per-user folder. Ported from
/// <c>rig.otel_stack.paths</c>; the layout is the Python's, so either finds what the other wrote.
/// </summary>
/// <param name="BaseDir">rig's per-user folder (<see cref="Roots.AppData.Current"/> in production).</param>
public sealed record OtelPaths(string BaseDir)
{
    /// <summary>One rotated JSON-lines file per signal; the un-suffixed name is the one being written.</summary>
    public static readonly IReadOnlyList<string> Signals = ["traces", "metrics", "logs"];

    /// <summary>The collector's own folder.</summary>
    public string Home => Path.Join(BaseDir, "otel");

    /// <summary>Where the file exporters write.</summary>
    public string Data => Path.Join(Home, "data");

    /// <summary>The collector config, regenerated on every start.</summary>
    public string Config => Path.Join(Home, "otelcol.yaml");

    /// <summary>The OTLP/HTTP endpoint the collector also forwards to; absent means no forwarding.</summary>
    public string Forward => Path.Join(Home, "forward");

    /// <summary>Written when <c>collector start</c> runs the binary itself (Windows, or macOS without the agent).</summary>
    public string PidFile => Path.Join(Home, "otelcol.pid");

    /// <summary>The collector's log.</summary>
    public string Log => Path.Join(BaseDir, "logs", "otelcol.log");

    /// <summary>Everything the on-demand ClickHouse pair writes: configs, pids, backfill state, and its data.</summary>
    public string ClickHouseHome => Path.Join(Home, "clickhouse");

    /// <summary>ClickHouse's data folder.</summary>
    public string ClickHouseData => Path.Join(ClickHouseHome, "data");

    /// <summary>ClickHouse's log.</summary>
    public string ClickHouseLog => Path.Join(BaseDir, "logs", "clickhouse.log");

    /// <summary>The contrib collector's log.</summary>
    public string ContribLog => Path.Join(BaseDir, "logs", "otelcol-contrib.log");

    /// <summary>The file a signal is being written to now, in <paramref name="data"/> (default: <see cref="Data"/>).</summary>
    public string ActiveFile(string signal, string? data = null) => Path.Join(data ?? Data, $"{signal}.jsonl");
}
