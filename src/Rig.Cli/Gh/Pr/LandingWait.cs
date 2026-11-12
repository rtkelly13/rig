using System.Globalization;
using Rig.Cli.Gh.Threads;

namespace Rig.Cli.Gh.Pr;

/// <summary>How a wait ended. The value is the process exit code.</summary>
internal enum Outcome
{
    Merged = 0,

    /// <summary>The PR cannot land until someone acts.</summary>
    Blocked = 1,

    /// <summary>GitHub could not be read.</summary>
    Error = 2,

    /// <summary>Time ran out while the PR was still moving.</summary>
    Timeout = 3,
}

/// <summary>Waits <paramref name="delay"/>; injectable so tests never sleep.</summary>
internal delegate Task Sleeper(TimeSpan delay, CancellationToken cancellationToken);

/// <summary>Reads the PR once.</summary>
internal delegate Task<Observation> Observer(CancellationToken cancellationToken);

/// <summary>How long to wait, and how often to look.</summary>
internal sealed record Pace
{
    /// <summary>Give up after this long. Zero looks once and reports.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>How long a state that is stuck only if it lasts must last before the wait stops.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The gap after a poll that saw a change; each quiet poll multiplies it by <see cref="Growth"/>, up to <see cref="MaxPoll"/>.</summary>
    public TimeSpan FirstPoll { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxPoll { get; init; } = TimeSpan.FromSeconds(60);

    public double Growth { get; init; } = 1.5;

    /// <summary>Consecutive failed reads of GitHub tolerated mid-wait. The very first read is never retried.</summary>
    public int MaxErrors { get; init; } = 5;
}

/// <summary>What the wait is waiting on, reported whenever it changes.</summary>
internal sealed record Tick(TimeSpan Elapsed, string Message, TimeSpan NextPoll);

/// <summary>How the wait ended. <see cref="Last"/> is the last successful observation; null when GitHub was never read.</summary>
internal sealed record WaitResult(Outcome Outcome, string Reason, string Message, TimeSpan Waited, int Polls, Observation? Last);

/// <summary>The poll loop behind <c>rig gh pr wait</c>: observe, assess, back off, until the outcome is known.</summary>
internal sealed class LandingWait(TimeProvider time, Sleeper sleep)
{
    /// <summary>An unsettled verdict is rechecked at least this often.</summary>
    private static readonly TimeSpan SettlePoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Polls until the PR merges, cannot land without someone acting, or <see cref="Pace.Timeout"/> passes. The gap between
    /// polls starts at <see cref="Pace.FirstPoll"/>, grows while nothing changes and resets when something does, so a
    /// change is seen quickly and a long quiet wait costs few requests.
    /// </summary>
    public async Task<WaitResult> RunAsync(Observer observe, Pace pace, Func<Tick, Task> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observe);
        ArgumentNullException.ThrowIfNull(pace);
        ArgumentNullException.ThrowIfNull(report);
        var start = time.GetTimestamp();
        var delay = pace.FirstPoll;
        var (polls, errors, wasQueued) = (0, 0, false);
        Observation? last = null;
        Verdict? verdict = null;
        (string Reason, TimeSpan Since)? unsettled = null;
        var shown = string.Empty;
        while (true)
        {
            string message;
            try
            {
                var seen = await observe(cancellationToken).ConfigureAwait(false);
                polls++;
                errors = 0;
                last = seen;
                verdict = LandingModel.Assess(seen, wasQueued);
                wasQueued = wasQueued || seen.Snapshot.Queue is not null;
                var at = time.GetElapsedTime(start);
                if (verdict.Kind is VerdictKind.Unsettled && unsettled?.Reason != verdict.Reason)
                {
                    unsettled = (verdict.Reason, at);
                }
                else if (verdict.Kind is not VerdictKind.Unsettled)
                {
                    unsettled = null;
                }

                var settled = unsettled is { } lasting && at - lasting.Since >= pace.Settle;
                if (verdict.Kind is VerdictKind.Merged)
                {
                    return new WaitResult(Outcome.Merged, verdict.Reason, verdict.Message, at, polls, last);
                }

                if (verdict.Kind is VerdictKind.Stuck || settled)
                {
                    return new WaitResult(Outcome.Blocked, verdict.Reason, verdict.Message, at, polls, last);
                }

                message = unsettled is null
                    ? verdict.Message
                    : string.Create(CultureInfo.InvariantCulture, $"{verdict.Message} (confirming for {pace.Settle.TotalSeconds:0}s before giving up)");
            }
            catch (GitHubException problem)
            {
                errors++;
                if (last is null || errors > pace.MaxErrors)
                {
                    return new WaitResult(Outcome.Error, Reason.GitHubError, problem.Message, time.GetElapsedTime(start), polls, last);
                }

                message = $"GitHub read failed ({errors} in a row), retrying: {problem.Message}";
            }

            var now = time.GetElapsedTime(start);
            var remaining = pace.Timeout - now;
            if (remaining <= TimeSpan.Zero)
            {
                // Out of time on a state that only looked stuck: nothing was seen moving, so call it blocked.
                return verdict is not null && unsettled is not null
                    ? new WaitResult(Outcome.Blocked, verdict.Reason, verdict.Message, now, polls, last)
                    : new WaitResult(Outcome.Timeout, verdict!.Reason, $"timed out; last seen: {verdict.Message}", now, polls, last);
            }

            var changed = message != shown;
            if (changed)
            {
                delay = pace.FirstPoll;
            }

            var pause = delay < remaining ? delay : remaining;
            if (unsettled is not null && pause > SettlePoll)
            {
                pause = SettlePoll;
            }

            if (changed)
            {
                await report(new Tick(now, message, pause)).ConfigureAwait(false);
                shown = message;
            }

            await sleep(pause, cancellationToken).ConfigureAwait(false);
            var grown = delay * pace.Growth;
            delay = grown < pace.MaxPoll ? grown : pace.MaxPoll;
        }
    }
}
