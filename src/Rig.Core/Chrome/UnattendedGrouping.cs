using System.Diagnostics;
using System.Globalization;

namespace Rig.Core.Chrome;

/// <summary>How the wait for an idle, unlocked machine went.</summary>
/// <param name="Deferred">Why grouping gave up; null when it may go ahead.</param>
/// <param name="Outcome"><c>clear_at_once</c>, <c>clear</c>, <c>gave_up</c> or <c>no_wait</c>.</param>
/// <param name="WaitedSeconds">How long it waited.</param>
/// <param name="Checks">How many times it looked.</param>
/// <param name="LastBlock">The last block's reason, if any was seen.</param>
public sealed record GroupingWait(string? Deferred, string Outcome, double WaitedSeconds = 0.0, int Checks = 1, string? LastBlock = null);

/// <summary>How grouping went over every attempt: input that stopped it is <paramref name="ByInput"/>, not a failure.</summary>
/// <param name="Planned">Moves in the first plan.</param>
/// <param name="Moved">Tabs moved over every attempt.</param>
/// <param name="Created">A new group was made.</param>
/// <param name="Stopped">Why it stopped; null when it finished.</param>
/// <param name="ByInput">The user's keyboard or mouse stopped it.</param>
/// <param name="Retries">Attempts after the first.</param>
public sealed record GroupingResult(int Planned, int Moved = 0, bool Created = false, string? Stopped = null, bool ByInput = false, int Retries = 0)
{
    /// <summary>
    /// The run's outcome for the trace: <c>deferred</c>, <c>nothing_to_group</c>, <c>grouped</c>, <c>interrupted</c>
    /// (by input) or <c>stopped</c>.
    /// </summary>
    public string Outcome(string? deferred) =>
        deferred is not null ? "deferred"
        : Planned == 0 ? "nothing_to_group"
        : Stopped is null ? "grouped"
        : ByInput ? "interrupted"
        : "stopped";
}

/// <summary>
/// Unattended grouping: wait for an idle machine before taking the mouse, and when the user's input stops grouping,
/// leave a note and go again once the machine has been left alone. Ported from the running half of
/// <c>desktop_manager.chrome.unattended</c>.
/// </summary>
/// <param name="runner">Carries out a plan.</param>
/// <param name="presence">The machine's idle clock, lock and devices.</param>
/// <param name="say">Writes a line for the user.</param>
/// <param name="sleep">Sleeps for so many seconds.</param>
/// <param name="clock">Seconds on a monotonic clock.</param>
public sealed class UnattendedGrouping(
    GroupRunner runner,
    IPresence presence,
    Action<string> say,
    Action<double> sleep,
    Func<double> clock)
{
    /// <summary>After input stops grouping, it goes again once the machine has been left alone this long.</summary>
    public const double RetryIdleMinutes = 0.5;

    /// <summary>How long a retry waits for that idle spell before leaving grouping to the next run.</summary>
    public const double RetryWaitMinutes = 5.0;

    /// <summary>Retries a run makes after input stops it, before leaving the rest to the next run.</summary>
    public const int InputRetries = 2;

    /// <summary>The note's file name.</summary>
    public const string PausedNote = "chrome-tidy-paused.txt";

    private static readonly ActivitySource Source = new("Rig.Chrome");

    /// <summary>The note asking the user to leave the machine alone so grouping can finish.</summary>
    public static IReadOnlyList<string> PausedNoteLines { get; } =
    [
        "Chrome tidy paused",
        string.Empty,
        "You used the keyboard or mouse while it was grouping tabs, so it stopped.",
        $"Leave the machine alone for {Whole(RetryIdleMinutes * 60)} seconds and it carries on by itself;",
        $"it waits up to {Short(RetryWaitMinutes)} minutes, then leaves the rest to the next hourly run.",
        string.Empty,
        "You can close this note.",
    ];

    /// <summary>
    /// Waits until an unattended run may take the mouse; <see cref="GroupingWait.Deferred"/> says why it gave up, if it
    /// did. One span for the whole wait, with a <c>check</c> event for every look.
    /// </summary>
    public GroupingWait Await(Func<IEnumerable<ChromeWindow>> readWindows, double idleMinutes, double waitMinutes)
    {
        using var activity = Source.StartActivity("wait_for_idle");
        activity?.SetTag("wait.budget_s", waitMinutes * 60);
        activity?.SetTag("wait.idle_minutes", idleMinutes);
        Block? Blocked()
        {
            var block = Unattended.GroupBlock(presence, readWindows, idleMinutes);
            activity?.AddEvent(new ActivityEvent("check", tags: CheckTags(block)));
            return block;
        }

        GroupingWait result;
        if (Blocked() is not { } first)
        {
            result = new GroupingWait(null, "clear_at_once");
        }
        else if (waitMinutes <= 0)
        {
            result = new GroupingWait(first.Reason, "no_wait", LastBlock: first.Reason);
        }
        else
        {
            say($"Waiting up to {Short(waitMinutes)} min to group: {first.Reason}");
            var outcome = Presence.WaitUntilClear(Blocked, waitMinutes * 60, sleep, clock);
            var checks = outcome.Checks + 1;
            if (outcome.Ready)
            {
                say($"Clear to group after {Whole(outcome.WaitedSeconds)}s ({outcome.Checks} checks)");
                result = new GroupingWait(null, "clear", outcome.WaitedSeconds, checks, outcome.LastBlock ?? first.Reason);
            }
            else
            {
                var reason = $"still not clear after {Short(waitMinutes)} min ({outcome.Checks} checks): {outcome.LastBlock}";
                result = new GroupingWait(reason, "gave_up", outcome.WaitedSeconds, checks, outcome.LastBlock);
            }
        }

        activity?.SetTag("wait.outcome", result.Outcome);
        activity?.SetTag("wait.seconds", Math.Round(result.WaitedSeconds, 1));
        activity?.SetTag("wait.checks", result.Checks);
        if (result.LastBlock is not null)
        {
            activity?.SetTag("wait.last_block", result.LastBlock);
        }

        return result;
    }

    /// <summary>
    /// Runs the plan. Unattended, input that stops it opens the note (<paramref name="showPausedNote"/>), and it goes
    /// again once the machine has been left alone <see cref="RetryIdleMinutes"/>, waiting up to
    /// <see cref="RetryWaitMinutes"/>, and planning afresh; after <see cref="InputRetries"/> it leaves the rest.
    /// </summary>
    /// <param name="plan">The first plan.</param>
    /// <param name="readWindows">Reads Chrome's windows, for a phase named in the trace.</param>
    /// <param name="replan">Plans afresh, for a phase named in the trace.</param>
    /// <param name="unattended">Nobody asked for this run, so the user's input stops it.</param>
    /// <param name="showPausedNote">Opens the note asking to be left alone.</param>
    public GroupingResult GroupUntilLeftAlone(
        GroupPlan plan,
        Func<string, IReadOnlyList<ChromeWindow>> readWindows,
        Func<string, GroupPlan> replan,
        bool unattended,
        Action showPausedNote)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(readWindows);
        ArgumentNullException.ThrowIfNull(replan);
        ArgumentNullException.ThrowIfNull(showPausedNote);
        var planned = plan.Moves.Count;
        var moved = 0;
        var created = false;
        var retries = 0;
        while (true)
        {
            var stop = unattended ? new InputStop(new InputWatch(presence)) : null;
            var ran = runner.Run(plan, stop is null ? null : stop.Check);
            moved += ran.Moved;
            created = created || (ran.Moved > 0 && plan.Moves.Any(move => move.New));
            var byInput = stop is { Fired: true };
            var result = new GroupingResult(planned, moved, created, ran.Stopped, byInput, retries);
            if (ran.Stopped is null || !byInput)
            {
                return result;
            }

            if (retries == InputRetries)
            {
                return result with { Stopped = $"{ran.Stopped} again after {retries} retries" };
            }

            showPausedNote();
            say(
                $"Paused grouping: {ran.Stopped}; goes again after {Whole(RetryIdleMinutes * 60)}s idle " +
                $"(waits up to {Short(RetryWaitMinutes)} min)");
            var wait = Await(() => readWindows("wait_check"), RetryIdleMinutes, RetryWaitMinutes);
            if (wait.Deferred is not null)
            {
                return result with { Stopped = $"{ran.Stopped}, then {wait.Deferred}" };
            }

            plan = replan("retry");
            retries++;
            if (plan.Moves.Count == 0)
            {
                return result with { Stopped = null, ByInput = false, Retries = retries };
            }
        }
    }

    /// <summary>
    /// For unattended runs: true, having said why, when a call is live and the run must leave Chrome alone. Tags
    /// <paramref name="run"/> with the outcome.
    /// </summary>
    public bool SkipIfLive(IEnumerable<ChromeWindow> windows, Activity? run = null)
    {
        if (Unattended.LiveCall(presence, windows) is not { } live)
        {
            return false;
        }

        say($"Skipped: {live}; left everything for the next run");
        run?.SetTag("run.outcome", "skipped_live");
        run?.SetTag("run.skipped", live);
        return true;
    }

    private static ActivityTagsCollection CheckTags(Block? block)
    {
        if (block is null)
        {
            return new() { ["blocked"] = false };
        }

        ActivityTagsCollection tags = new() { ["blocked"] = true, ["reason"] = block.Reason };
        if (block.ClearsIn is { } hint)
        {
            tags["hint_s"] = Math.Round(hint, 1);
        }

        return tags;
    }

    // Python's "{:.0f}": round half to even, no decimals.
    private static string Whole(double value) => Math.Round(value, MidpointRounding.ToEven).ToString("F0", CultureInfo.InvariantCulture);

    // Python's "{:g}" for the small round numbers these are.
    private static string Short(double value) => value.ToString(CultureInfo.InvariantCulture);
}
