using Rig.Core.Processes;

namespace Rig.Core.Jobs;

/// <summary>The Windows Task Scheduler, as far as <c>jobs status</c> asks it: whether a task is registered.</summary>
public interface IScheduledTasks
{
    /// <summary>Whether the task <paramref name="name"/> (folder included) is registered.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>The tasks <c>desktop-manager jobs install</c> registers. Names match <c>desktop_manager.jobs.install</c>.</summary>
public static class CoordinatorTasks
{
    /// <summary>The Task Scheduler folder that holds them.</summary>
    public const string Folder = "Shared Utilities";

    /// <summary>The sign-in run.</summary>
    public const string Logon = Folder + "\\Logon";

    /// <summary>The run every <see cref="Job.TickMinutes"/>.</summary>
    public const string Tick = Folder + "\\Tick";

    /// <summary>The display guard.</summary>
    public const string Guard = Folder + "\\Display Guard";

    /// <summary>All three, in the order <c>jobs status</c> names any missing.</summary>
    public static IReadOnlyList<string> All { get; } = [Logon, Tick, Guard];
}

/// <summary>Asks <c>schtasks /Query</c>, which exits 0 only for a registered task. It reads and never changes anything.</summary>
public sealed class Schtasks(IProc proc) : IScheduledTasks
{
    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        (await proc.RunAsync(new ProcRequest(["schtasks", "/Query", "/TN", name]), cancellationToken).ConfigureAwait(false)).Ok;
}

/// <summary>When this machine booted, as <c>desktop_manager.jobs.session_windows.boot_time</c> answers it.</summary>
public static class BootTime
{
    /// <summary>
    /// <paramref name="now"/> less <paramref name="uptime"/> on Windows; the epoch elsewhere, where nothing schedules the
    /// jobs, so every once-per-boot job looks done.
    /// </summary>
    public static DateTimeOffset At(DateTimeOffset now, TimeSpan uptime, bool windows) => windows ? now - uptime : DateTimeOffset.UnixEpoch;
}
