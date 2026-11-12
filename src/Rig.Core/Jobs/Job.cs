namespace Rig.Core.Jobs;

/// <summary>What starts a coordinator run: signing in, or the scheduled tick every <see cref="Job.TickMinutes"/>.</summary>
public enum JobTrigger
{
    /// <summary>The run at sign-in.</summary>
    Logon,

    /// <summary>The run every <see cref="Job.TickMinutes"/>.</summary>
    Tick,
}

/// <summary>How often a job runs on the triggers it lists.</summary>
public enum JobCadence
{
    /// <summary>Every time one of its triggers fires.</summary>
    EveryRun,

    /// <summary>On the first of its triggers after a boot.</summary>
    OncePerBoot,

    /// <summary>When its last start is <see cref="Job.DailyHours"/> old.</summary>
    Daily,
}

/// <summary>
/// One recurring thing that keeps the machine aligned, run by the coordinator instead of a scheduled task of its own.
/// Ported from <c>desktop_manager.jobs.registry.Job</c>.
/// </summary>
/// <param name="Name">The job's name, as <c>--only</c> and the state file know it.</param>
/// <param name="Description">One line on what it does.</param>
/// <param name="Argv">The command the runner starts.</param>
/// <param name="Triggers">The triggers it runs on.</param>
/// <param name="Cadence">How often, on those triggers.</param>
/// <param name="TimeoutMinutes">How long the runner lets it go before stopping it.</param>
public sealed record Job(
    string Name,
    string Description,
    IReadOnlyList<string> Argv,
    IReadOnlySet<JobTrigger> Triggers,
    JobCadence Cadence,
    int TimeoutMinutes)
{
    /// <summary>How often the Tick task fires.</summary>
    public const int TickMinutes = 15;

    /// <summary>How old a daily job's last start must be before it runs again.</summary>
    public const int DailyHours = 20;

    /// <summary>Paths that must exist for the job to run; one missing skips it, rather than failing it.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    /// <summary>Run only once nobody has touched the keyboard or mouse for this long; 0 runs regardless.</summary>
    public int IdleMinutes { get; init; }

    /// <summary>Exit codes meaning "could not run yet": a tick reruns the job until it exits otherwise, once per boot.</summary>
    public IReadOnlySet<int> RetryExitCodes { get; init; } = new HashSet<int>();

    /// <summary>The <c>--- </c>-headed log the job writes its own narrative to, when that is not stdout.</summary>
    public string? DetailLog { get; init; }

    /// <summary>The rig flag that must be on for the job to run; naming the job in <c>--only</c> runs it anyway.</summary>
    public string? Feature { get; init; }

    /// <summary>The triggers as <c>jobs status</c> lists them: <c>logon, tick</c>.</summary>
    public string TriggerList => string.Join(", ", Triggers.Select(JobWords.Wire).Order(StringComparer.Ordinal));
}

/// <summary>The words the state files, the CLI and Python use for triggers and cadences.</summary>
public static class JobWords
{
    /// <summary>The triggers' words, in the order help lists them.</summary>
    public static IReadOnlyList<string> Triggers { get; } = ["logon", "tick"];

    /// <summary><paramref name="trigger"/> as written: <c>logon</c> or <c>tick</c>.</summary>
    public static string Wire(JobTrigger trigger) => trigger == JobTrigger.Logon ? "logon" : "tick";

    /// <summary><paramref name="cadence"/> as written: <c>every-run</c>, <c>once-per-boot</c> or <c>daily</c>.</summary>
    public static string Wire(JobCadence cadence) => cadence switch
    {
        JobCadence.EveryRun => "every-run",
        JobCadence.OncePerBoot => "once-per-boot",
        _ => "daily",
    };

    /// <summary>The trigger <paramref name="word"/> names; anything but <c>logon</c> is the tick.</summary>
    public static JobTrigger Trigger(string word) => word == "logon" ? JobTrigger.Logon : JobTrigger.Tick;
}
