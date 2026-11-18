using System.Text.Json;
using Rig.Core.Estate;
using Rig.Core.Jobs;
using Rig.Core.Orca;
using Rig.Core.Otel;
using Rig.Core.Processes;
using Rig.Core.Roots;
using Rig.Core.Workspace;

using Rig.Web.Ops;

namespace Rig.Web.Mcp;

/// <summary>What needs a person: jobs whose last run failed, and estate decisions waiting on someone.</summary>
/// <param name="FailingJobs">Jobs whose last run failed, as <c>list_jobs</c> shows them.</param>
/// <param name="Decisions">The estate decision feed.</param>
public sealed record AttentionView(IReadOnlyList<JobView> FailingJobs, DecisionFeedView Decisions);

/// <summary>The disk holding the ops tools' code root.</summary>
/// <param name="Path">The folder measured.</param>
/// <param name="Total">Its capacity in bytes.</param>
/// <param name="Used">Bytes in use.</param>
/// <param name="Free">Bytes available.</param>
public sealed record DiskUsageView(string Path, long Total, long Used, long Free);

/// <summary>
/// The ops data the <c>rig web mcp</c> tools answer, built by the same view builders the JSON routes use, so an agent
/// and the ops pages never disagree. Every source is injectable for tests.
/// </summary>
public sealed class RigOps : IDisposable
{
    /// <summary>The server's name, as an MCP client sees it.</summary>
    public const string ServerName = "rig-ops";

    /// <summary>The server's version.</summary>
    public const string ServerVersion = "1.0.0";

    /// <summary>How many runs <c>list_runs</c> shows unless asked for another number.</summary>
    public const int RunLimit = 50;

    private readonly Lock _lock = new();
    private readonly Func<IReadOnlyList<Job>> _jobs;
    private readonly string _directory;
    private readonly Func<IReadOnlyList<string>, Task<int>> _start;
    private readonly Func<string?, IReadOnlyList<string>> _command;
    private readonly string _feedPath;
    private readonly string _root;
    private readonly TrimCache _trims;
    private readonly Func<Telemetry> _makeTelemetry;
    private Telemetry? _telemetry;

    /// <summary>Builds the tools over the seams.</summary>
    public RigOps(
        Func<IReadOnlyList<Job>> jobs,
        string directory,
        Func<IReadOnlyList<string>, Task<int>> start,
        Func<string?, IReadOnlyList<string>> command,
        string feedPath,
        string root,
        TrimCache trims,
        Func<Telemetry> makeTelemetry)
    {
        _jobs = jobs;
        _directory = directory;
        _start = start;
        _command = command;
        _feedPath = feedPath;
        _root = root;
        _trims = trims;
        _makeTelemetry = makeTelemetry;
    }

    /// <summary>The real sources for a rig at <paramref name="checkout"/>.</summary>
    public static RigOps ForCheckout(string checkout)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var windows = OperatingSystem.IsWindows();
        var appData = AppData.Current();
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var codeRoot = CodeRoot.Current();
        var site = JobsSite.Locate(
            checkout,
            appData,
            JobRegistry.DefaultPlanPath(Environment.GetEnvironmentVariable("APPDATA"), home, windows),
            Path.Join(codeRoot, "personal"));
        var machineConfig = ConfigUnder(Environment.GetEnvironmentVariable("WORKSPACE_HOME"), home);
        var environment = new JobsEnvironment(
            Rig.Core.Runtime.RuntimeManager.PublishedFolder(checkout),
            site.Plan,
            MachineConfig.WorkspaceRepo(machineConfig, codeRoot, home),
            site.PersonalRoot,
            MachineConfig.WorkDirs(machineConfig, codeRoot, home),
            Rig.Core.Estate.Decisions.DefaultFeedPath(localAppData, home),
            OrcaLocations.Current.EstateUserData(),
            windows);
        var jobs = JobRegistry.Default(environment);
        var proc = new SystemProc();
        var git = new Git(proc, codeRoot);
        var trim = new Trim(git, TimeProvider.System);
        var discovery = new CloneDiscovery(CloneDiscovery.CacheFileUnder(home), TimeProvider.System);
        var trims = TrimCache.ForEstate(codeRoot, trim, () => discovery.Repos(codeRoot).Paths, new TreeRemoval(proc));
        var detector = OperatingSystem.IsWindows() ? (IDetachedStart)new WindowsDetachedStart() : new UnixDetachedStart(proc);
        return new RigOps(
            () => jobs,
            JobState.JobsDir(appData),
            async argv => await detector.StartAsync(argv, windows ? "NUL" : "/dev/null").ConfigureAwait(false),
            job =>
            {
                List<string> argv = [environment.Script("rig"), "jobs", "run", "--trigger", "tick"];
                if (job is not null)
                {
                    argv.Add("--only");
                    argv.Add(job);
                }

                return argv;
            },
            environment.DecisionsFeed,
            codeRoot,
            trims,
            () => Telemetry.ForCheckout(checkout));
    }

    /// <summary>The tools for this machine, as a checkout launch finds it.</summary>
    public static RigOps Default() => ForCheckout(Environment.CurrentDirectory);

    /// <summary>What needs a person: coordinator jobs whose last run failed, and estate decisions waiting on someone.</summary>
    public string Attention()
    {
        var failing = JobViews.Build(_jobs(), _directory).Where(view => view.Last is { Ok: false }).ToList();
        return OpsJson.Serialize(new AttentionView(failing, DecisionFeed.Read(_feedPath)));
    }

    /// <summary>Every registered job: what it does, when it runs, and how its last run went.</summary>
    public string ListJobs() => OpsJson.Serialize(JobViews.Build(_jobs(), _directory));

    /// <summary>Recorded runs, newest first; <paramref name="job"/> narrows it and <paramref name="limit"/> caps it.</summary>
    public string ListRuns(string? job = null, int limit = RunLimit)
    {
        var trimmed = string.IsNullOrWhiteSpace(job) ? null : job.Trim();
        return OpsJson.Serialize(JobViews.Runs(_directory, trimmed, Clamp(limit, 1, JobViews.MaxLimit)));
    }

    /// <summary>What one run printed: its <c>output</c> is null when it saved none or it was pruned.</summary>
    public string RunOutput(string job, string runId) => OpsJson.Serialize(JobViews.Output(_jobs(), _directory, job, runId));

    /// <summary>Starts a coordinator run in the background; it shows in <c>list_runs</c> when it finishes.</summary>
    public async Task<string> StartRunAsync(string? job = null)
    {
        var trimmed = string.IsNullOrWhiteSpace(job) ? null : job.Trim();
        if (trimmed is not null && !_jobs().Any(item => item.Name == trimmed))
        {
            throw new KeyNotFoundException($"No job named '{trimmed}'");
        }

        var pid = await _start(_command(trimmed)).ConfigureAwait(false);
        return OpsJson.Serialize(new RunStarted(trimmed, pid));
    }

    /// <summary>The decision feed, with a note saying why it is empty when it is.</summary>
    public string Decisions() => OpsJson.Serialize(DecisionFeed.Read(_feedPath));

    /// <summary>Total, used and free bytes on the drive holding the code root.</summary>
    public string DiskUsage() => OpsJson.Serialize(Disk(_root));

    /// <summary>The last finished trim scan, starting a new one when <paramref name="refresh"/> asks.</summary>
    public string TrimCandidates(bool refresh = false) => OpsJson.Serialize(_trims.Current(refresh));

    /// <summary>Deletes caches the last finished scan offered; each is rechecked first and refused if it changed.</summary>
    public async Task<string> ApplyTrimAsync(IReadOnlyList<string> paths)
    {
        return OpsJson.Serialize(await _trims.ApplyAsync(paths).ConfigureAwait(false));
    }

    /// <summary>Which trace store answers and which trace explorers are up.</summary>
    public async Task<string> TelemetryStatusAsync() => OpsJson.Serialize(await Source().StatusAsync().ConfigureAwait(false));

    /// <summary>Recent traces, newest first, from ClickHouse when it answers and the local NDJSON files otherwise.</summary>
    public async Task<string> ListTracesAsync(int days = 1, int limit = 100, string? service = null)
    {
        var query = new TraceQuery(
            Clamp(days, 1, 30),
            Clamp(limit, 1, 500),
            string.IsNullOrWhiteSpace(service) ? null : service.Trim());
        return OpsJson.Serialize(await Source().RecentAsync(query).ConfigureAwait(false));
    }

    /// <summary>Stops the telemetry client this instance created, if any.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _telemetry?.Dispose();
            _telemetry = null;
        }

        _trims.Dispose();
    }

    private Telemetry Source()
    {
        lock (_lock)
        {
            _telemetry ??= _makeTelemetry();
            return _telemetry;
        }
    }

    private static DiskUsageView Disk(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        var drive = new DriveInfo(root);
        var free = drive.AvailableFreeSpace;
        return new DiskUsageView(path, drive.TotalSize, drive.TotalSize - free, free);
    }

    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(high, value));

    // The workspace config: $WORKSPACE_HOME/config.json, else ~/.workspace/config.json.
    private static string ConfigUnder(string? workspaceHome, string home)
    {
        var folder = string.IsNullOrEmpty(workspaceHome)
            ? Path.Join(home, ".workspace")
            : workspaceHome.StartsWith('~') ? Path.Join(home, workspaceHome[1..]) : workspaceHome;
        return Path.Join(folder, "config.json");
    }
}

/// <summary>The ops answers' JSON: snake_case keys, nulls kept, compact like pydantic's <c>model_dump_json</c>.</summary>
internal static class OpsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    /// <summary><paramref name="value"/> as an ops answer.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
