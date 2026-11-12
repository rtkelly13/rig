namespace Rig.Core.Jobs;

/// <summary>
/// Where the jobs' commands and files live on one machine. Ported from <c>desktop_manager.jobs.registry.Environment</c>.
/// </summary>
/// <param name="Scripts">The rig environment's scripts folder: <c>desktop-manager</c>, <c>rig</c> and <c>python</c>.</param>
/// <param name="Plan">The workspace plan restore and align read.</param>
/// <param name="WorkspaceRoot">The work workspace checkout (<c>tools.estate.workspace_repo</c>); null leaves its jobs out.</param>
/// <param name="PersonalRoot">The folder holding the personal clones; its parent is the code root every estate job scans.</param>
/// <param name="WorkRoots">This machine's work folders; <c>estate decisions</c> skips any that are absent.</param>
/// <param name="DecisionsFeed">Where <c>estate decisions</c> writes its feed.</param>
/// <param name="OrcaUserData">The Orca instance that holds the personal estate.</param>
/// <param name="ChromeTabsLog">The log the scheduled Chrome tab organise writes its own narrative to.</param>
/// <param name="Windows">Whether the scripts carry <c>.exe</c> and paths compare without case.</param>
public sealed record JobsEnvironment(
    string Scripts,
    string Plan,
    string? WorkspaceRoot,
    string PersonalRoot,
    IReadOnlyList<string> WorkRoots,
    string DecisionsFeed,
    string OrcaUserData,
    string ChromeTabsLog,
    bool Windows)
{
    /// <summary>The folder holding <c>personal</c> and the org and work folders beside it.</summary>
    public string CodeRoot => Path.GetDirectoryName(PersonalRoot) ?? PersonalRoot;

    /// <summary>The code root, plus the work and workspace folders when they live outside it.</summary>
    public IReadOnlyList<string> EstateRoots =>
        [CodeRoot, .. WorkRoots.Concat(WorkspaceRoot is null ? [] : [WorkspaceRoot]).Where(root => !IsUnder(root, CodeRoot))];

    /// <summary>The scripts folder's <paramref name="name"/>, with <c>.exe</c> on Windows.</summary>
    public string Script(string name) => Path.Join(Scripts, Windows ? name + ".exe" : name);

    /// <summary>The workspace checkout's own interpreter; its venv is a Windows one wherever this runs.</summary>
    public string WorkspacePython => Path.Join(Workspace(), ".venv", "Scripts", "python.exe");

    /// <summary>The workspace checkout's <c>maintain</c> entry point.</summary>
    public string WorkspaceMaintain => Path.Join(Workspace(), "src", "workspace_cli", "maintain", "__main__.py");

    /// <summary>The workspace checkout's CLI entry point.</summary>
    public string WorkspaceCli => Path.Join(Workspace(), "src", "workspace_cli", "main.py");

    private string Workspace() =>
        WorkspaceRoot ?? throw new InvalidOperationException("No workspace repo is configured (tools.estate.workspace_repo)");

    // Lexical, as pathlib's is_relative_to: no resolving, and case only matters off Windows.
    private bool IsUnder(string path, string parent)
    {
        var comparison = Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string[] inner = Parts(path);
        string[] outer = Parts(parent);
        return inner.Length >= outer.Length && outer.Select((part, i) => string.Equals(part, inner[i], comparison)).All(same => same);
    }

    private string[] Parts(string path)
    {
        var parts = path.Split(Windows ? ['/', '\\'] : ['/'], StringSplitOptions.RemoveEmptyEntries);
        return path.StartsWith('/') || (Windows && path.StartsWith('\\')) ? ["/", .. parts] : parts;
    }
}

/// <summary>The jobs the coordinator runs, in the order a run takes them. Ported from <c>desktop_manager.jobs.registry</c>.</summary>
public static class JobRegistry
{
    /// <summary><c>claude-sessions startup</c> typed nothing: the workstation was locked or the plan's screens were missing.</summary>
    public const int ClaudeSessionsDeferred = 4;

    /// <summary><c>workspace restore</c> found fewer screens than the plan places windows on and opened nothing.</summary>
    public const int RestoreDeferred = 4;

    /// <summary>An Orca command found no running Orca to talk to.</summary>
    public const int OrcaUnavailable = 3;

    /// <summary>How long <c>align</c> waits for the keyboard and mouse to go quiet.</summary>
    public const int AlignIdleMinutes = 3;

    /// <summary>The rig flag the Claude session restore sits behind.</summary>
    public const string ClaudeSessionRestoreFlag = "claude-session-restore";

    private static readonly HashSet<JobTrigger> Logon = [JobTrigger.Logon];
    private static readonly HashSet<JobTrigger> Tick = [JobTrigger.Tick];
    private static readonly HashSet<JobTrigger> Both = [JobTrigger.Logon, JobTrigger.Tick];

    /// <summary>
    /// The per-user workspace plan, as <c>desktop_manager.plan.default_plan_path</c> finds it: under <c>APPDATA</c> on
    /// Windows when it is set, else <c>~/.config/workspace-manager</c>.
    /// </summary>
    public static string DefaultPlanPath(string? appData, string home, bool windows) =>
        windows && !string.IsNullOrEmpty(appData)
            ? Path.Join(appData, "workspace-manager", "windows-workspace.json")
            : Path.Join(home, ".config", "workspace-manager", "windows-workspace.json");

    /// <summary>Where the scheduled Chrome tab organise logs, as <c>desktop_manager.chrome.install_location</c> puts it.</summary>
    public static string ChromeTabsLog(string? localAppData, string home) =>
        Path.Join(string.IsNullOrEmpty(localAppData) ? Path.Join(home, "AppData", "Local") : localAppData, "shared-utilities", "chrome-tabs", "organise.log");

    /// <summary>Every job for <paramref name="env"/>, in run order; the workspace ones only when it names a workspace repo.</summary>
    public static IReadOnlyList<Job> Default(JobsEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        var desktopManager = env.Script("desktop-manager");
        var rig = env.Script("rig");
        var python = Path.Join(env.Scripts, env.Windows ? "python.exe" : "python");
        var plan = env.Plan;
        var code = env.CodeRoot;
        List<Job> jobs =
        [
            new("restore", "Open and place every window in the workspace plan.",
                [desktopManager, "workspace", "restore", "--config", plan, "--defer-logon-commands"], Logon, JobCadence.EveryRun, 45)
            {
                Requires = [plan],
                RetryExitCodes = new HashSet<int> { RestoreDeferred },
            },
            new("claude-sessions", "Resume Claude sessions the last shutdown cut off; start `claude` in empty terminals.",
                [desktopManager, "claude-sessions", "startup", "--config", plan], Logon, JobCadence.EveryRun, 20)
            {
                Requires = [plan],
                RetryExitCodes = new HashSet<int> { ClaudeSessionsDeferred },
                Feature = ClaudeSessionRestoreFlag,
            },
            new("chrome-organise", "Close merged, duplicate and superseded Chrome tabs, and group the rest, once idle.",
                [python, "-m", "desktop_manager.chrome.scheduled", "--wait-minutes", $"{Job.TickMinutes - 5}"], Tick, JobCadence.EveryRun,
                Job.TickMinutes + 5)
            {
                DetailLog = env.ChromeTabsLog,
            },
            new("align", "Put open plan windows back on their desktop and slot, launching nothing.",
                [desktopManager, "workspace", "align", "--config", plan], Tick, JobCadence.EveryRun, 5)
            {
                Requires = [plan],
                IdleMinutes = AlignIdleMinutes,
            },
            new("estate-sync", "`git fetch --prune` across every clone in the estate, so later checks see gone branches.",
                [rig, "estate", "sync", "--root", code], Both, JobCadence.Daily, 30),
            new("estate-risk", "Report clones across the estate holding uncommitted or unpushed work.",
                [rig, "estate", "risk", "--root", code], Both, JobCadence.Daily, 30),
            new("estate-trim", "Delete node_modules, venvs and build output in estate worktrees idle 7+ days.",
                [rig, "estate", "trim", "--apply", "--root", code], Both, JobCadence.Daily, 30),
            new("estate-orca", "Register every cloned managed project in the personal Orca, in its group.",
                [rig, "estate", "orca", "--apply", "--code-root", code, "--orca-data", env.OrcaUserData], Both, JobCadence.Daily, 10)
            {
                RetryExitCodes = new HashSet<int> { OrcaUnavailable },
            },
            new("orca-sync", "Apply config/orca.json to the personal Orca: agent default args, repo colours, scripts, refs.",
                [rig, "workspace", "orca", "sync", "--apply", "--root", env.PersonalRoot, "--orca-data", env.OrcaUserData], Both,
                JobCadence.Daily, 10)
            {
                RetryExitCodes = new HashSet<int> { OrcaUnavailable },
            },
            new("otel-collector", "Keep the local OTel collector listening on 127.0.0.1:4317/4318; prune its files past 7 days.",
                [rig, "otel-stack", "collector", "ensure"], Both, JobCadence.EveryRun, 5),
            new("workload-sample", "Sample memory use by app (Claude sessions, large processes, bursts); keep 7 days of samples.",
                [rig, "workload", "sample"], Both, JobCadence.EveryRun, 2),
            new("estate-prune-caches", "Prune the uv, pnpm, NuGet http and Docker build caches, and Claude temp idle 7+ days.",
                [rig, "estate", "prune-caches", "--apply"], Both, JobCadence.Daily, 60),
        ];
        if (env.WorkspaceRoot is not null)
        {
            jobs.Add(new("worktree-reclaim", "Remove merged worktrees across every work repo (`workspace worktree reclaim`).",
                [env.WorkspacePython, "-m", "workspace_cli.main", "worktree", "reclaim", "--all-repos", "--apply"], Both, JobCadence.Daily, 60)
            {
                Requires = [env.WorkspacePython, env.WorkspaceCli],
            });
        }

        jobs.Add(new("estate-decisions", "Write the feed of worktrees and directories only a human can decide about.",
            [rig, "estate", "decisions", .. env.EstateRoots.SelectMany(root => new[] { "--root", root }), "--write", env.DecisionsFeed], Both,
            JobCadence.Daily, 30));
        if (env.WorkspaceRoot is not null)
        {
            jobs.Add(new("workspace-maintain", "The workspace repo's own due housekeeping (`workspace maintain run --due`).",
                [env.WorkspacePython, "-m", "workspace_cli.maintain", "run", "--due"], Both, JobCadence.EveryRun, 60)
            {
                Requires = [env.WorkspacePython, env.WorkspaceMaintain],
            });
        }

        return jobs;
    }
}
