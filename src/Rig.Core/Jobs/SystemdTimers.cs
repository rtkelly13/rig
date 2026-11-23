using System.Globalization;
using System.Text;
using Rig.Core.Processes;

namespace Rig.Core.Jobs;

/// <summary>
/// The systemd user units that start the coordinator on Linux: <c>rig-jobs-logon.service</c>, started with the user's
/// session (<c>default.target</c>), and <c>rig-jobs-tick.service</c>, started by <c>rig-jobs-tick.timer</c> shortly after
/// sign-in and every <see cref="Job.TickMinutes"/> minutes after its last run. Both are one-shot runs of
/// <c>rig jobs run --scheduled</c> at background priority, given the installing shell's <c>PATH</c>: the user manager's
/// own would not find <c>git</c>, <c>gh</c> or <c>uv</c>. Their output goes to the journal.
/// </summary>
public static class JobsSystemdUnit
{
    /// <summary>The sign-in unit's name, as <c>jobs status</c> lists it.</summary>
    public const string Logon = "Logon";

    /// <summary>The every-<see cref="Job.TickMinutes"/>-minutes unit's name.</summary>
    public const string Tick = "Tick";

    /// <summary>How long after the user manager starts the first tick runs, so it follows the logon run.</summary>
    public const int FirstTickMinutes = 2;

    /// <summary>Both, in the order <c>jobs status</c> names any missing.</summary>
    public static IReadOnlyList<string> All { get; } = [Logon, Tick];

    /// <summary>The service that runs <paramref name="name"/>'s trigger: <c>rig-jobs-logon.service</c> or <c>rig-jobs-tick.service</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string Service(string name) => $"rig-jobs-{Wire(name)}.service";

    /// <summary>The tick's timer, <c>rig-jobs-tick.timer</c>.</summary>
    public const string TickTimer = "rig-jobs-tick.timer";

    /// <summary>The unit enabling puts in place for <paramref name="name"/>: the logon service, or the tick's timer.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string Enabled(string name) => Wire(name) == "tick" ? TickTimer : Service(name);

    /// <summary>Every unit file <paramref name="name"/> owns.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static IReadOnlyList<string> Files(string name) => Wire(name) == "tick" ? [Service(name), TickTimer] : [Service(name)];

    /// <summary>
    /// The service unit for <paramref name="name"/>: a one-shot run of rig on its trigger. The logon one is wanted by
    /// <c>default.target</c>; the tick one has no install section, because its timer starts it.
    /// </summary>
    /// <param name="name">The unit.</param>
    /// <param name="target">The paths the unit runs rig from.</param>
    /// <param name="pathValue">The <c>PATH</c> the run inherits; null omits it.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string RenderService(string name, JobsInstallTarget target, string? pathValue)
    {
        ArgumentNullException.ThrowIfNull(target);
        var trigger = Wire(name);
        var text = new StringBuilder()
            .Append("[Unit]\n")
            .Append("Description=rig jobs: ").Append(trigger).Append(" run\n")
            .Append('\n')
            .Append("[Service]\n")
            .Append("Type=oneshot\n")
            .Append("ExecStart=").AppendJoin(' ', new[] { target.Rig, "jobs", "run", "--trigger", trigger, "--scheduled" }.Select(Quote)).Append('\n')
            .Append("WorkingDirectory=").Append(Quote(target.Root)).Append('\n');
        if (pathValue is { Length: > 0 } path)
        {
            text.Append("Environment=").Append(Quote("PATH=" + path)).Append('\n');
        }

        text.Append("Nice=10\n").Append("IOSchedulingClass=idle\n");
        if (trigger == "logon")
        {
            text.Append('\n').Append("[Install]\n").Append("WantedBy=default.target\n");
        }

        return text.ToString();
    }

    /// <summary>The tick's timer: first <see cref="FirstTickMinutes"/> minutes after sign-in, then <see cref="Job.TickMinutes"/> after each run.</summary>
    public static string RenderTimer() => new StringBuilder()
        .Append("[Unit]\n")
        .Append("Description=rig jobs: tick every ").Append(Job.TickMinutes.ToString(CultureInfo.InvariantCulture)).Append(" minutes\n")
        .Append('\n')
        .Append("[Timer]\n")
        .Append("OnStartupSec=").Append(FirstTickMinutes.ToString(CultureInfo.InvariantCulture)).Append("min\n")
        .Append("OnUnitActiveSec=").Append(Job.TickMinutes.ToString(CultureInfo.InvariantCulture)).Append("min\n")
        .Append("AccuracySec=1min\n")
        .Append("Unit=").Append(Service(Tick)).Append('\n')
        .Append('\n')
        .Append("[Install]\n")
        .Append("WantedBy=timers.target\n")
        .ToString();

    /// <summary>The trigger word the unit runs: <c>logon</c> or <c>tick</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string Wire(string name) => name switch
    {
        Logon => "logon",
        Tick => "tick",
        _ => throw new ArgumentException("Unknown jobs systemd unit", nameof(name)),
    };

    // systemd expands % specifiers and $ variables in these lines, so both are doubled; an argument with a space or a
    // quote is double-quoted with C escapes.
    private static string Quote(string argument)
    {
        var escaped = argument.Replace("%", "%%", StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal);
        return escaped.Any(c => c is ' ' or '"' or '\\' or '\'' or '\t')
            ? '"' + escaped.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"'
            : escaped;
    }
}

/// <summary>Installs, queries and removes the coordinator's systemd user units through <c>systemctl --user</c>.</summary>
/// <param name="proc">Runs <c>systemctl</c>.</param>
/// <param name="unitsDir">The user unit folder, <c>~/.config/systemd/user</c>.</param>
/// <param name="pathValue">The <c>PATH</c> each run inherits, as the installer's own; null omits it.</param>
public sealed class SystemdTimers(IProc proc, string unitsDir, string? pathValue) : IJobsScheduler
{
    /// <inheritdoc />
    public JobsPlatform Platform => JobsPlatform.Linux;

    /// <inheritdoc />
    public IReadOnlyList<string> Names => JobsSystemdUnit.All;

    /// <inheritdoc />
    public IReadOnlyList<string> Retired => [];

    /// <inheritdoc />
    public string? StartsAfterInstall => null;

    /// <inheritdoc />
    public async Task RegisterAsync(string name, JobsInstallTarget target, string userId, DateTimeOffset start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var tick = JobsSystemdUnit.Wire(name) == "tick";
        _ = Directory.CreateDirectory(unitsDir);
        await File.WriteAllTextAsync(Path.Join(unitsDir, JobsSystemdUnit.Service(name)), JobsSystemdUnit.RenderService(name, target, pathValue), cancellationToken).ConfigureAwait(false);
        if (tick)
        {
            await File.WriteAllTextAsync(Path.Join(unitsDir, JobsSystemdUnit.TickTimer), JobsSystemdUnit.RenderTimer(), cancellationToken).ConfigureAwait(false);
        }

        await SystemctlAsync(cancellationToken, "daemon-reload").ConfigureAwait(false);

        // The timer starts now so the tick runs this session; the logon service waits for the next sign-in.
        await (tick
            ? SystemctlAsync(cancellationToken, "enable", "--now", JobsSystemdUnit.TickTimer)
            : SystemctlAsync(cancellationToken, "enable", JobsSystemdUnit.Service(name))).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        (await proc.RunAsync(new ProcRequest(["systemctl", "--user", "is-enabled", "--quiet", JobsSystemdUnit.Enabled(name)]), cancellationToken).ConfigureAwait(false)).Ok;

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var files = JobsSystemdUnit.Files(name).Select(file => Path.Join(unitsDir, file)).Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            return false;
        }

        _ = await proc.RunAsync(new ProcRequest(["systemctl", "--user", "disable", "--now", JobsSystemdUnit.Enabled(name)]), cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            File.Delete(file);
        }

        _ = await proc.RunAsync(new ProcRequest(["systemctl", "--user", "daemon-reload"]), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public Task StartAsync(string name, CancellationToken cancellationToken = default) =>
        SystemctlAsync(cancellationToken, "start", "--no-block", JobsSystemdUnit.Service(name));

    private async Task SystemctlAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        string[] argv = ["systemctl", "--user", .. arguments];
        (await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false)).EnsureOk(argv);
    }
}
