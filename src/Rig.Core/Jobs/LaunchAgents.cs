using Rig.Core.Services;

namespace Rig.Core.Jobs;

/// <summary>
/// The two macOS LaunchAgents that start the coordinator: <c>com.rtkelly13.rig.jobs.logon</c> at sign-in and
/// <c>com.rtkelly13.rig.jobs.tick</c> every <see cref="Job.TickMinutes"/> minutes. Both are one-shot launches of
/// <c>rig jobs run --scheduled</c>, not resident processes, so they are marked as background housekeeping and given the
/// installing shell's <c>PATH</c>; launchd's own default would not find <c>git</c>, <c>gh</c> or <c>uv</c>.
/// </summary>
public static class JobsLaunchAgent
{
    /// <summary>Every agent's label starts with this.</summary>
    public const string LabelPrefix = "com.rtkelly13.rig.jobs";

    /// <summary>The sign-in agent's name, as <c>jobs status</c> lists it.</summary>
    public const string Logon = "Logon";

    /// <summary>The every-<see cref="Job.TickMinutes"/>-minutes agent's name.</summary>
    public const string Tick = "Tick";

    /// <summary>Both, in the order <c>jobs status</c> names any missing.</summary>
    public static IReadOnlyList<string> All { get; } = [Logon, Tick];

    /// <summary>The agent's label, <c>com.rtkelly13.rig.jobs.logon</c> or <c>...tick</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string Label(string name) => $"{LabelPrefix}.{Wire(name)}";

    /// <summary>The plist's path in <paramref name="agentsDir"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string PlistPath(string agentsDir, string name) => Path.Join(agentsDir, Label(name) + ".plist");

    /// <summary>
    /// The agent's plist: the rig command on its trigger, run at login and, for the tick, every
    /// <see cref="Job.TickMinutes"/> minutes. Both outputs go to <paramref name="log"/>.
    /// </summary>
    /// <param name="name">The agent.</param>
    /// <param name="target">The paths the agent runs rig from.</param>
    /// <param name="log">The file its output goes to.</param>
    /// <param name="pathValue">The <c>PATH</c> the run inherits; null omits it.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static byte[] Render(string name, JobsInstallTarget target, string log, string? pathValue)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(log);
        var tick = name == Tick;
        var entries = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Label"] = Label(name),
            ["ProgramArguments"] = new[] { target.Rig, "jobs", "run", "--trigger", Wire(name), "--scheduled" },
            ["WorkingDirectory"] = target.Root,
            ["RunAtLoad"] = true,
            ["ProcessType"] = "Background",
            ["LowPriorityIO"] = true,
            ["Nice"] = 10,
            ["StandardOutPath"] = log,
            ["StandardErrorPath"] = log,
        };
        if (tick)
        {
            entries["StartInterval"] = Job.TickMinutes * 60;
        }

        if (pathValue is { Length: > 0 } path)
        {
            entries["EnvironmentVariables"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["PATH"] = path };
        }

        return Plist.Render(entries);
    }

    /// <summary>The trigger word the agent runs: <c>logon</c> or <c>tick</c>.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not one of <see cref="All"/>.</exception>
    public static string Wire(string name) => name switch
    {
        Logon => "logon",
        Tick => "tick",
        _ => throw new ArgumentException("Unknown jobs LaunchAgent", nameof(name)),
    };
}

/// <summary>Installs, queries and removes the coordinator's LaunchAgents through one launchd domain.</summary>
/// <param name="launchd">Resolves the launchd service the agents live in; the domain's uid is looked up once, on first use.</param>
/// <param name="logDirectory">Where each agent's output goes, <c>~/Library/Logs</c>.</param>
/// <param name="pathValue">The <c>PATH</c> each run inherits, as the installer's own; null omits it.</param>
public sealed class LaunchAgents(Func<CancellationToken, Task<Launchd>> launchd, string logDirectory, string? pathValue) : IJobsScheduler
{
    private Launchd? resolved;

    /// <inheritdoc />
    public JobsPlatform Platform => JobsPlatform.MacOS;

    /// <inheritdoc />
    public IReadOnlyList<string> Names => JobsLaunchAgent.All;

    /// <inheritdoc />
    public IReadOnlyList<string> Retired => [];

    /// <inheritdoc />
    public string? StartsAfterInstall => null;

    /// <inheritdoc />
    public async Task RegisterAsync(string name, JobsInstallTarget target, string userId, DateTimeOffset start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var service = await ServiceAsync(cancellationToken).ConfigureAwait(false);
        _ = Directory.CreateDirectory(logDirectory);
        var log = Path.Join(logDirectory, $"rig-jobs-{JobsLaunchAgent.Wire(name)}.log");
        await service.LoadAsync(JobsLaunchAgent.PlistPath(service.AgentsDir, name), JobsLaunchAgent.Render(name, target, log, pathValue), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default) =>
        await (await ServiceAsync(cancellationToken).ConfigureAwait(false)).LoadedAsync(JobsLaunchAgent.Label(name), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        var service = await ServiceAsync(cancellationToken).ConfigureAwait(false);
        return await service.RemoveAsync(JobsLaunchAgent.PlistPath(service.AgentsDir, name), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StartAsync(string name, CancellationToken cancellationToken = default) =>
        await (await ServiceAsync(cancellationToken).ConfigureAwait(false)).KickstartAsync(JobsLaunchAgent.Label(name), cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task<Launchd> ServiceAsync(CancellationToken cancellationToken) =>
        resolved ??= await launchd(cancellationToken).ConfigureAwait(false);
}
