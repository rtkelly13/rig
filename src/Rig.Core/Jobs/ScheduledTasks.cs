using System.Text;
using Rig.Core.Processes;

namespace Rig.Core.Jobs;

/// <summary>The Windows Task Scheduler operations used by the jobs commands.</summary>
public interface IScheduledTasks
{
    /// <summary>Whether the task <paramref name="name"/> (folder included) is registered.</summary>
    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes the named task; false when it is absent or Task Scheduler refuses the deletion.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Registers or replaces a named task from its XML, or throws when Task Scheduler refuses.</summary>
    Task RegisterAsync(string name, string xml, CancellationToken cancellationToken = default);

    /// <summary>Starts a registered task, or throws when Task Scheduler refuses.</summary>
    Task StartAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>The tasks <c>rig jobs install</c> registers, preserving the original coordinator task names.</summary>
public static class CoordinatorTasks
{
    /// <summary>The Task Scheduler folder that holds them.</summary>
    public const string Folder = "Rig";

    /// <summary>The folder they were registered in before the repo was renamed.</summary>
    // TODO(2027-01-31): delete with the rest of the shared-utilities -> rig migration (AppDataMigration, the legacy task names) once every machine has run `rig runtime install` and `rig jobs install`.
    public const string LegacyFolder = "Shared Utilities";

    /// <summary>The sign-in run.</summary>
    public const string Logon = Folder + "\\Logon";

    /// <summary>The run every <see cref="Job.TickMinutes"/>.</summary>
    public const string Tick = Folder + "\\Tick";

    /// <summary>The display guard.</summary>
    public const string Guard = Folder + "\\Display Guard";

    /// <summary>All three, in the order <c>jobs status</c> names any missing.</summary>
    public static IReadOnlyList<string> All { get; } = [Logon, Tick, Guard];

    /// <summary>
    /// The tasks <c>jobs install</c> deletes: the standalone ones the coordinator replaced, and the coordinator's own
    /// tasks under <see cref="LegacyFolder"/>, which run the runtime copy in the old app-data folder.
    /// </summary>
    public static IReadOnlyList<string> Retired { get; } =
    [
        "Workspace Restore",
        "Chrome Tabs Organise",
        "Chrome Tabs Prune",
        "Workspace Display Guard",
        LegacyFolder + "\\Logon",
        LegacyFolder + "\\Tick",
        LegacyFolder + "\\Display Guard",
    ];
}

/// <summary>Registers, starts, queries and deletes named tasks through <c>schtasks</c>.</summary>
public sealed class Schtasks(IProc proc, string scratchDirectory) : IScheduledTasks
{
    /// <inheritdoc />
    public async Task RegisterAsync(string name, string xml, CancellationToken cancellationToken = default)
    {
        var directory = Path.Join(scratchDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Join(directory, "task.xml");
            await File.WriteAllTextAsync(path, xml, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
            string[] argv = ["schtasks", "/Create", "/TN", name, "/XML", path, "/F"];
            (await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false)).EnsureOk(argv);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(string name, CancellationToken cancellationToken = default)
    {
        string[] argv = ["schtasks", "/Run", "/TN", name];
        (await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false)).EnsureOk(argv);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        (await proc.RunAsync(new ProcRequest(["schtasks", "/Query", "/TN", name]), cancellationToken).ConfigureAwait(false)).Ok;

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        (await proc.RunAsync(new ProcRequest(["schtasks", "/Delete", "/TN", name, "/F"]), cancellationToken).ConfigureAwait(false)).Ok;
}

/// <summary>When this machine booted.</summary>
public static class BootTime
{
    /// <summary>
    /// <paramref name="now"/> less <paramref name="uptime"/> on Windows; the epoch elsewhere, where nothing schedules the
    /// jobs, so every once-per-boot job looks done.
    /// </summary>
    public static DateTimeOffset At(DateTimeOffset now, TimeSpan uptime, bool windows) => windows ? now - uptime : DateTimeOffset.UnixEpoch;
}
