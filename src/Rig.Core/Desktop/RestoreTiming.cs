using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Desktop;

/// <summary>
/// Times restore phases: each is an activity, logged when slow, and listed in one closing line. Ported from
/// <c>desktop_manager.restore_timing.StepTimer</c>, on the port's clock rather than <c>perf_counter</c> so a fake
/// desktop's sleeps count.
/// </summary>
/// <param name="log">Where slow steps and the summary go.</param>
/// <param name="scope">The <c>scope</c> tag on every activity.</param>
/// <param name="clock">The time now.</param>
public sealed partial class StepTimer(IRunLog log, string scope, Func<TimeSpan> clock)
{
    /// <summary>A restore phase taking longer than this is logged as slow when it ends.</summary>
    public static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(15);

    /// <summary>A desktop launches and places every entry on it, so it gets longer before it counts as slow.</summary>
    public static readonly TimeSpan SlowDesktop = TimeSpan.FromSeconds(90);

    /// <summary>The source restore's activities come from.</summary>
    public static readonly ActivitySource Source = new("Rig.Desktop.Restore");

    private readonly List<string> _taken = [];

    /// <summary>Each step's name and time so far, in the order they ended.</summary>
    public IReadOnlyList<string> Taken => _taken;

    /// <summary>Start timing <paramref name="span"/>; disposing the result ends it.</summary>
    /// <param name="span">The activity name; less its <c>restore.</c> prefix, the step's name.</param>
    /// <param name="label">The step's name, when not derived from <paramref name="span"/>.</param>
    /// <param name="slowAfter">How long before it counts as slow; <see cref="SlowStep"/> by default.</param>
    /// <param name="tags">Extra activity tags.</param>
    public TimedStep Step(string span, string? label = null, TimeSpan? slowAfter = null, IEnumerable<KeyValuePair<string, object?>>? tags = null)
    {
        ArgumentNullException.ThrowIfNull(span);
        var name = label ?? (span.StartsWith("restore.", StringComparison.Ordinal) ? span["restore.".Length..] : span);
        var activity = Source.StartActivity(span);
        if (activity is not null)
        {
            activity.SetTag("scope", scope);
            foreach (var (key, value) in tags ?? [])
            {
                activity.SetTag(key, value);
            }
        }

        return new TimedStep(this, name, clock(), slowAfter ?? SlowStep, activity);
    }

    /// <summary>Log every step's time in one line, if any ran.</summary>
    public void LogSummary()
    {
        if (_taken.Count > 0)
        {
            log.Log($"Step timings: {string.Join(", ", _taken)}");
        }
    }

    /// <summary>
    /// A token that marks one terminal launch so its window can be told apart: the entry's name as a slug, then eight
    /// random hex digits. Ported from <c>new_terminal_restore_token</c>.
    /// </summary>
    public static string NewTerminalRestoreToken(LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var slug = NotSlug().Replace(PyText.CaseFold(entry.Name), "-").Trim('-');
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{(slug.Length > 0 ? slug : "terminal")}-{suffix}";
    }

    /// <summary>How long to wait for a launched entry's window: its own timeout, else 10 seconds.</summary>
    public static TimeSpan EntryWindowWaitTimeout(LaunchEntry entry) => OwnTimeout(entry) ?? TimeSpan.FromSeconds(10);

    /// <summary>
    /// Extra window wait while the entry's process is already running. At logon a process can exist long before it
    /// paints (Fork took over 20s), and timing out then makes the retry launch a second instance. Terminals get none:
    /// they are matched by token and title, so a missing window means the launch failed.
    /// </summary>
    public static TimeSpan EntryWindowWaitGrace(LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Type == LaunchEntry.Terminal ? TimeSpan.Zero : TimeSpan.FromSeconds(40);
    }

    /// <summary>The ceiling on waiting for a terminal's title to change: its own timeout, else 15 seconds.</summary>
    public static TimeSpan EntryTitleTakeoverTimeout(LaunchEntry entry) => OwnTimeout(entry) ?? TimeSpan.FromSeconds(15);

    /// <summary>A Windows Terminal PowerShell entry, which restore always relaunches and polls for readiness.</summary>
    public static bool ShouldForceTerminalRelaunch(LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Type == LaunchEntry.Terminal && entry.Shell == LaunchEntry.DefaultTerminalShell;
    }

    private static TimeSpan? OwnTimeout(LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.WindowWaitTimeoutMs is { } ms and > 0 ? TimeSpan.FromMilliseconds(ms) : null;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    internal void Ended(string name, TimeSpan started, TimeSpan slowAfter)
    {
        var seconds = (clock() - started).TotalSeconds;
        var slow = seconds > slowAfter.TotalSeconds;
        var took = seconds.ToString("F1", CultureInfo.InvariantCulture);
        _taken.Add($"{name} {took}s" + (slow ? " (slow)" : string.Empty));
        if (slow)
        {
            log.Log($"Slow step: {name} took {took}s (over {slowAfter.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)}s)");
        }
    }
}

/// <summary>One running step; disposing it ends the step.</summary>
public sealed class TimedStep : IDisposable
{
    private readonly StepTimer _timer;
    private readonly string _name;
    private readonly TimeSpan _started;
    private readonly TimeSpan _slowAfter;
    private readonly Activity? _activity;
    private bool _done;

    internal TimedStep(StepTimer timer, string name, TimeSpan started, TimeSpan slowAfter, Activity? activity)
    {
        _timer = timer;
        _name = name;
        _started = started;
        _slowAfter = slowAfter;
        _activity = activity;
    }

    /// <summary>Tag the step's activity with what it found.</summary>
    public void Set(string key, object? value) => _activity?.SetTag(key, value);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_done)
        {
            return;
        }

        _done = true;
        try
        {
            _timer.Ended(_name, _started, _slowAfter);
        }
        finally
        {
            _activity?.Dispose();
        }
    }
}
