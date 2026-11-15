using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Rig.Web.Mcp;

/// <summary>
/// The <c>rig web mcp</c> tools: the ops data for agents, answering what the <c>/api/ops/...</c> routes answer. Each
/// expects the JSON text the JSON routes would send, so an agent and the pages never disagree.
/// </summary>
/// <remarks>
/// | tool | same as |
/// |---|---|
/// | <c>attention</c> | jobs whose last run failed, and decisions waiting on a person |
/// | <c>list_jobs</c> | <c>GET /api/ops/jobs</c> |
/// | <c>list_runs</c> | <c>GET /api/ops/runs?job=&amp;limit=</c> |
/// | <c>run_output</c> | <c>GET /api/ops/runs/&lt;job&gt;/&lt;run_id&gt;/output</c> |
/// | <c>start_run</c> | <c>POST /api/ops/jobs/run</c> |
/// | <c>decisions</c> | <c>GET /api/ops/decisions</c> |
/// | <c>disk_usage</c> | <c>GET /api/ops/estate/disk</c> |
/// | <c>trim_candidates</c> | <c>GET /api/ops/estate/trim</c> |
/// | <c>apply_trim</c> | <c>POST /api/ops/estate/trim/apply</c> |
/// | <c>telemetry_status</c> | <c>GET /api/ops/telemetry/status</c> |
/// | <c>list_traces</c> | <c>GET /api/ops/telemetry/traces</c> |
/// </remarks>
/// <param name="ops">The data source; every source behind it is injectable for tests.</param>
[McpServerToolType]
public sealed class RigOpsTools(RigOps ops)
{
    /// <summary>What needs a person: failed jobs and pending decisions.</summary>
    [McpServerTool(Name = "attention", ReadOnly = true)]
    [Description("What needs a person: coordinator jobs whose last run failed, and estate decisions waiting on someone.")]
    public string Attention() => ops.Attention();

    /// <summary>Every registered coordinator job.</summary>
    [McpServerTool(Name = "list_jobs", ReadOnly = true)]
    [Description("Every registered coordinator job: what it does, when it runs, and how its last run went.")]
    public string ListJobs() => ops.ListJobs();

    /// <summary>Recorded job runs, newest first.</summary>
    [McpServerTool(Name = "list_runs", ReadOnly = true)]
    [Description("Recorded job runs, newest first, each with its trace id when it ran traced.")]
    public string ListRuns(
        [Description("Only this job's runs; empty for every job.")] string? job = null,
        [Description("How many runs, 1 to 1000.")] int limit = RigOps.RunLimit) =>
        ops.ListRuns(job, limit);

    /// <summary>What one job run printed.</summary>
    [McpServerTool(Name = "run_output", ReadOnly = true)]
    [Description("What one job run printed; output is null when the run saved none or it has been pruned.")]
    [SuppressMessage("Naming", "CA1707", Justification = "The MCP argument is run_id, as the route path and the Python server spell it.")]
    public string RunOutput(
        [Description("The job's name, as list_jobs gives it.")] string job,
        [Description("The run's id, as list_runs gives it.")] string run_id)
    {
        try
        {
            return ops.RunOutput(job, run_id);
        }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>Starts a coordinator run in the background.</summary>
    [McpServerTool(Name = "start_run", ReadOnly = false, Destructive = false, Idempotent = false)]
    [Description("Start a coordinator run in the background and return at once; it shows in list_runs when it finishes. If a scheduled run is in progress, this one skips.")]
    public async Task<string> StartRunAsync(
        [Description("One job to run whether or not it is due; empty to run every job that is due.")] string? job = null)
    {
        try
        {
            return await ops.StartRunAsync(job).ConfigureAwait(false);
        }
        catch (KeyNotFoundException e)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>The decision feed.</summary>
    [McpServerTool(Name = "decisions", ReadOnly = true)]
    [Description("Worktrees waiting on a person: unpushed commits, closed PRs, idle checkouts. note says why it is empty.")]
    public string Decisions() => ops.Decisions();

    /// <summary>The drive holding the code root.</summary>
    [McpServerTool(Name = "disk_usage", ReadOnly = true)]
    [Description("Total, used and free bytes on the drive holding the code root.")]
    public string DiskUsage() => ops.DiskUsage();

    /// <summary>The last finished trim scan.</summary>
    [McpServerTool(Name = "trim_candidates", ReadOnly = true)]
    [Description("Regenerable caches (node_modules, .venv, bin, obj) in idle worktrees, from the last finished scan. A scan runs in the background: while scanning is true, call again for the finished list.")]
    public string TrimCandidates(
        [Description("Start a new scan rather than returning the last one.")] bool refresh = false) =>
        ops.TrimCandidates(refresh);

    /// <summary>Deletes caches the last scan offered.</summary>
    [McpServerTool(Name = "apply_trim", Destructive = true, Idempotent = true)]
    [Description("Delete caches the last finished scan offered; each is rechecked first and refused if it changed.")]
    public async Task<string> ApplyTrimAsync(
        [Description("Candidate paths exactly as trim_candidates gave them.")] IReadOnlyList<string> paths) =>
        await ops.ApplyTrimAsync(paths).ConfigureAwait(false);

    /// <summary>Which trace store and explorers answer.</summary>
    [McpServerTool(Name = "telemetry_status", ReadOnly = true)]
    [Description("Which trace store answers (ClickHouse or local NDJSON files) and which trace explorers are up.")]
    public async Task<string> TelemetryStatusAsync() => await ops.TelemetryStatusAsync().ConfigureAwait(false);

    /// <summary>Recent traces, newest first.</summary>
    [McpServerTool(Name = "list_traces", ReadOnly = true)]
    [Description("Recent traces, newest first, from ClickHouse when it answers and local NDJSON files otherwise.")]
    public async Task<string> ListTracesAsync(
        [Description("How far back, 1 to 30.")] int days = 1,
        [Description("How many traces, 1 to 500.")] int limit = 100,
        [Description("Only this service's traces; empty for all.")] string? service = null) =>
        await ops.ListTracesAsync(days, limit, service).ConfigureAwait(false);
}
