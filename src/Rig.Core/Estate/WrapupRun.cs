using System.Security.Cryptography;
using System.Text;
using Rig.Core.Otel;
using Rig.Core.Processes;

namespace Rig.Core.Estate;

/// <summary>Time for the rolling queue, injectable so tests never sleep: monotonic seconds for deadlines, wall time for comparing with Orca.</summary>
public interface IWrapupClock
{
    /// <summary>Now, in UTC.</summary>
    DateTimeOffset Now { get; }

    /// <summary>Seconds on a clock that only goes forward.</summary>
    double Monotonic { get; }

    /// <summary>Waits <paramref name="delay"/>.</summary>
    Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The real clock, over <see cref="TimeProvider"/>.</summary>
/// <param name="time">The time source.</param>
public sealed class SystemWrapupClock(TimeProvider time) : IWrapupClock
{
    /// <inheritdoc />
    public DateTimeOffset Now => time.GetUtcNow();

    /// <inheritdoc />
    public double Monotonic => time.GetTimestamp() / (double)time.TimestampFrequency;

    /// <inheritdoc />
    public Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, time, cancellationToken);
}

/// <summary>What <c>report</c> did: the answer it wrote, or why it wrote nothing.</summary>
/// <param name="Answer">The answer written.</param>
/// <param name="Error">Why nothing was written.</param>
public sealed record WrapupReported(AnswerRecord? Answer, string Error = "");

/// <summary>
/// Sends the prompts, writes what was seen to each worktree's <c>temp/.rig/wrapup.json</c>, and records an agent's own
/// verdict (<c>estate wrapup report</c>). Ported from <c>wrapup.py</c>, <c>wrapup_record.py</c>, <c>wrapup_report.py</c>
/// and <c>wrapup_watch.py</c>.
/// </summary>
public static class WrapupRun
{
    /// <summary>Seconds between polls of <c>orca worktree ps</c>.</summary>
    public const double PollSeconds = 5.0;

    /// <summary>The record of sending the prompt to <paramref name="row"/> at <paramref name="at"/>, under the id the prompt carried.</summary>
    public static AskRecord AskFor(WrapupRow row, string at, string? template)
    {
        ArgumentNullException.ThrowIfNull(row);
        var sha = template is null ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(template)))[..12];
        return new AskRecord(at, row.Pane, row.Session, row.Facts?.Head ?? "", Wrapup.DescribeFacts(row.Facts, row.Pr), sha, row.AskId);
    }

    /// <summary>Writes new verdicts, and asks sent this run, to each worktree's state file. A worktree whose state cannot be written gets a note saying why.</summary>
    public static async Task<IReadOnlyList<WrapupRow>> RecordAsync(
        IReadOnlyList<WrapupRow> rows, DateTimeOffset now, string? template, IWrapupStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(store);
        var at = WrapupStamps.Timestamp(now);
        var output = rows.ToList();
        var byPath = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        List<string> order = [];
        for (var i = 0; i < output.Count; i++)
        {
            if (!byPath.TryGetValue(output[i].Path, out var list))
            {
                byPath[output[i].Path] = list = [];
                order.Add(output[i].Path);
            }

            list.Add(i);
        }

        foreach (var path in order)
        {
            var indexes = byPath[path];
            List<object> additions = [];
            foreach (var i in indexes)
            {
                var row = output[i];
                if (row.Status == WrapupStatus.Sent)
                {
                    additions.Add(AskFor(row, at, template));
                }
                else if (NewAnswer(row) is { } answer)
                {
                    additions.Add(answer with { At = at });
                }
            }

            if (additions.Count == 0)
            {
                continue;
            }

            var first = output[indexes[0]];
            var state = store.Read(path) ?? WrapupState.Empty(first.WorktreeId, first.Branch);
            foreach (var item in additions)
            {
                if (item is AskRecord ask)
                {
                    state = state.WithAsk(ask);
                }
                else if (item is AnswerRecord answer && !AnsweredSince(state, answer))
                {
                    state = state.WithAnswer(answer);
                }
            }

            if (await store.WriteAsync(path, state, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                foreach (var i in indexes)
                {
                    var note = string.Join("; ", new[] { output[i].Note, $"state not recorded: {failure}" }.Where(x => x.Length > 0));
                    output[i] = output[i] with { Note = note };
                }
            }
        }

        return output;
    }

    /// <summary>Sends the prompt to every ask row, deepest first, and notes what happened.</summary>
    public static async Task<IReadOnlyList<WrapupRow>> ApplySendsAsync(
        IReadOnlyList<WrapupRow> rows,
        string template,
        IReadOnlyDictionary<string, WrapupWorktree> worktrees,
        WrapupOrca orca,
        Func<string> newAskId,
        string rigExecutable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(orca);
        ArgumentNullException.ThrowIfNull(newAskId);
        var output = rows.ToList();
        foreach (var i in Wrapup.SendOrder(output))
        {
            var row = output[i] with { AskId = newAskId() };
            if (row.Terminal is null)
            {
                output[i] = row with { Status = WrapupStatus.SendFailed, Note = "no terminal" };
                continue;
            }

            var failure = await orca.SendAsync(row.Terminal, Wrapup.RenderPrompt(template, row, output, worktrees, rigExecutable), cancellationToken).ConfigureAwait(false);
            output[i] = failure is null ? row with { Status = WrapupStatus.Sent } : row with { Status = WrapupStatus.SendFailed, Note = failure };
        }

        return output;
    }

    /// <summary>
    /// Asks every ask row, <see cref="WrapupRolling.MaxInFlight"/> at a time, recording each send and answer as it happens. Subtasks go
    /// first, and a parent waits while any of its subtasks is queued or outstanding. Cancelling defers what was not yet asked.
    /// </summary>
    public static async Task<IReadOnlyList<WrapupRow>> AskRollingAsync(
        IReadOnlyList<WrapupRow> rows,
        string template,
        IReadOnlyDictionary<string, WrapupWorktree> worktrees,
        WrapupRolling rolling,
        Action<WrapupRow, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(rolling);
        progress ??= static (_, _) => { };
        var clock = rolling.Clock;
        var output = rows.ToList();
        var queue = Wrapup.SendOrder(output).ToList();
        List<(int Index, DateTimeOffset SentAt, double Deadline)> flights = [];
        var width = Math.Max(rolling.MaxInFlight, 1);
        var wait = rolling.Wait;

        async Task SaveAsync(int i, string? templateText) =>
            output[i] = (await RecordAsync([output[i]], clock.Now, templateText, rolling.Store, CancellationToken.None).ConfigureAwait(false))[0];

        try
        {
            while (queue.Count > 0 || flights.Count > 0)
            {
                while (flights.Count < width && Next(queue, flights.Select(f => f.Index), output) is { } i)
                {
                    _ = queue.Remove(i);
                    var row = output[i] with { AskId = rolling.NewAskId() };
                    if (row.Terminal is null)
                    {
                        output[i] = row with { Status = WrapupStatus.SendFailed, Note = "no terminal" };
                        progress(output[i], "send failed: no terminal");
                        continue;
                    }

                    var failure = await rolling.Orca.SendAsync(row.Terminal, Wrapup.RenderPrompt(template, row, output, worktrees, rolling.RigExecutable), cancellationToken).ConfigureAwait(false);
                    if (failure is not null)
                    {
                        output[i] = row with { Status = WrapupStatus.SendFailed, Note = failure };
                        progress(output[i], $"send failed: {failure}");
                        continue;
                    }

                    var sentAt = clock.Now;
                    output[i] = row with { Status = WrapupStatus.Sent, Asked = AskFor(row, WrapupStamps.Timestamp(sentAt), template) };
                    await SaveAsync(i, template).ConfigureAwait(false);
                    flights.Add((i, sentAt, clock.Monotonic + wait.TotalSeconds));
                    progress(output[i], "asked");
                }

                if (flights.Count == 0)
                {
                    continue;
                }

                var earliest = flights.Min(f => f.Deadline);
                await clock.SleepAsync(TimeSpan.FromSeconds(Math.Max(0.0, Math.Min(PollSeconds, earliest - clock.Monotonic))), cancellationToken).ConfigureAwait(false);
                Dictionary<(string, string), WrapupAgent> agents;
                try
                {
                    agents = Agents(await rolling.Orca.ReadWorktreesAsync(cancellationToken).ConfigureAwait(false));
                }
                catch (WrapupException)
                {
                    agents = [];
                }

                foreach (var flight in flights.ToList())
                {
                    var row = output[flight.Index];
                    // The agent's own report wins; it is already in the file, so nothing is saved for it.
                    var finished = Reported(row, rolling.Store);
                    if (finished is null)
                    {
                        finished = Outcome(row, agents.GetValueOrDefault((row.WorktreeId, row.Pane)), flight.SentAt);
                        if (finished is not null && finished.Status == WrapupStatus.Answered)
                        {
                            output[flight.Index] = finished;
                            await SaveAsync(flight.Index, null).ConfigureAwait(false);
                            finished = output[flight.Index];
                        }
                    }

                    if (finished is not null)
                    {
                        output[flight.Index] = finished;
                        _ = flights.Remove(flight);
                        progress(finished, Said(finished));
                    }
                    else if (clock.Monotonic >= flight.Deadline)
                    {
                        output[flight.Index] = row with { Note = $"no answer within {Minutes(wait)}; the next run reads it" };
                        _ = flights.Remove(flight);
                        progress(output[flight.Index], $"no reply within {Minutes(wait)}, moving on");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var i in queue)
            {
                output[i] = output[i] with { Status = WrapupStatus.Deferred, Note = "interrupted before it was asked" };
            }
        }

        return output;
    }

    /// <summary>The command's <c>--wait</c> as the progress lines say it.</summary>
    public static string Minutes(TimeSpan wait)
    {
        var seconds = (long)wait.TotalSeconds;
        return seconds % 60 == 0 ? $"{seconds / 60}m" : $"{seconds}s";
    }

    /// <summary>The top-level folder of the git worktree holding <paramref name="directory"/>, or null outside one.</summary>
    public static async Task<string?> WorktreeRootAsync(Git git, string directory)
    {
        ArgumentNullException.ThrowIfNull(git);
        var top = await git.OutAsync(directory, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        return top.Length > 0 ? top : null;
    }

    /// <summary>Appends the agent's verdict to <paramref name="worktree"/>'s state file, linked to the ask it answers.</summary>
    public static async Task<WrapupReported> ReportAsync(
        string worktree, string askId, bool done, string pending, IReadOnlyList<string> todo, DateTimeOffset now, Func<Task<string>> head, IWrapupStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(todo);
        var state = store.Read(worktree);
        if (state is null || state.Asks.Count == 0)
        {
            return new(null, "no wrap-up ask is recorded in this worktree");
        }

        var ask = !string.IsNullOrEmpty(askId)
            ? state.AskById(askId)
            : state.Asks.Reverse().FirstOrDefault(a => state.AnswerTo(a) is null) ?? state.Asks[^1];
        if (ask is null)
        {
            return new(null, $"no ask {askId} is recorded in this worktree");
        }

        var answer = new AnswerRecord(
            WrapupStamps.Timestamp(now),
            ask.Pane,
            done ? "done" : "pending",
            done ? "" : pending.Trim() is { Length: > 0 } summary ? summary : string.Join("; ", todo),
            await head().ConfigureAwait(false),
            ask.At,
            todo,
            "report");
        var failure = await store.WriteAsync(worktree, state.WithAnswer(answer), cancellationToken).ConfigureAwait(false);
        return failure is not null ? new(null, failure) : new(answer);
    }

    internal static string Said(WrapupRow row)
    {
        if (row.Status == WrapupStatus.Waiting)
        {
            return "waiting on you" + (row.Note.Length > 0 ? $": {row.Note}" : "");
        }

        if (row.Verdict is null)
        {
            return "answered, without a WRAPUP line";
        }

        return row.Verdict.Done ? "answered: done" : $"answered: pending: {row.Verdict.Detail}";
    }

    /// <summary>The verdict on <paramref name="row"/> when the state file does not have it yet.</summary>
    private static AnswerRecord? NewAnswer(WrapupRow row)
    {
        if (row.Status != WrapupStatus.Answered || row.Verdict is null)
        {
            return null;
        }

        var verdict = row.Verdict.Done ? "done" : "pending";
        var askedAt = row.Asked?.At;
        var last = row.Recorded;
        if (last is not null && askedAt is not null && last.AskedAt == askedAt)
        {
            return null; // already recorded for this ask: by `report`, or by an earlier run
        }

        if (last is not null && last.AskedAt == askedAt && last.Verdict == verdict && last.Detail == row.Verdict.Detail)
        {
            return null;
        }

        // A reply read later says nothing of the commit it was written at. The ask's commit is the
        // conservative stand-in: if HEAD moved since, the inbox shows the verdict as older than the PR.
        var head = !string.IsNullOrEmpty(row.Asked?.Head) ? row.Asked.Head : row.Facts?.Head ?? "";
        return new AnswerRecord("", row.Pane, verdict, row.Verdict.Detail, head, askedAt);
    }

    /// <summary>Whether the agent reported on the same ask after this run read the file: its own word wins.</summary>
    private static bool AnsweredSince(WrapupState state, AnswerRecord answer) =>
        answer.AskedAt is not null && state.Answers.Any(a => a.AskedAt == answer.AskedAt && a.Pane == answer.Pane);

    private static HashSet<string> Descendants(WrapupRow row, List<WrapupRow> rows)
    {
        var children = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            _ = children.TryAdd(r.WorktreeId, r.Children);
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>(row.Children);
        while (stack.Count > 0)
        {
            var id = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (found.Add(id))
            {
                stack.AddRange(children.GetValueOrDefault(id, []));
            }
        }

        return found;
    }

    /// <summary>The first queued ask none of whose subtasks is still queued or outstanding.</summary>
    private static int? Next(List<int> queue, IEnumerable<int> flights, List<WrapupRow> rows)
    {
        var inFlight = flights.ToList();
        var pending = queue.Concat(inFlight).Select(i => rows[i].WorktreeId).ToHashSet(StringComparer.Ordinal);
        foreach (var i in queue)
        {
            if (!Descendants(rows[i], rows).Overlaps(pending))
            {
                return i;
            }
        }

        // Unreachable while subtask links form a tree; never stall on a cycle.
        return queue.Count > 0 && inFlight.Count == 0 ? queue[0] : null;
    }

    private static WrapupRow? Reported(WrapupRow row, IWrapupStore store)
    {
        var state = store.Read(row.Path);
        var ask = state?.AskById(row.AskId);
        var answer = ask is not null ? state!.AnswerTo(ask) : null;
        return answer is null
            ? null
            : row with { Status = WrapupStatus.Answered, Verdict = new WrapupVerdict(answer.Verdict == "done", answer.Detail), Recorded = answer, Note = "" };
    }

    private static WrapupRow? Outcome(WrapupRow row, WrapupAgent? agent, DateTimeOffset sentAt)
    {
        if (agent?.StateStartedAt is not { } started || started / 1000 <= sentAt.ToUnixTimeMilliseconds() / 1000.0)
        {
            return null;
        }

        if (agent.State == "waiting")
        {
            return row with { Status = WrapupStatus.Waiting, State = agent.State, Note = Wrapup.WaitingNote(agent) };
        }

        if (agent.State != "done" || !(agent.Prompt ?? "").TrimStart().StartsWith(Wrapup.Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var verdict = Wrapup.ParseVerdict(agent.LastMessage);
        var first = WrapupText.SplitLines(agent.LastMessage ?? "").Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var note = verdict is not null ? "" : $"replied without a WRAPUP line: {WrapupText.Head(first, 160)}";
        return row with { Status = WrapupStatus.Answered, State = agent.State, Verdict = verdict, Note = note };
    }

    private static Dictionary<(string, string), WrapupAgent> Agents(IReadOnlyList<WrapupWorktree> worktrees)
    {
        var agents = new Dictionary<(string, string), WrapupAgent>();
        foreach (var w in worktrees)
        {
            foreach (var a in w.Agents ?? [])
            {
                agents[(w.WorktreeId, a.PaneKey)] = a;
            }
        }

        return agents;
    }
}

/// <summary>What the rolling queue runs with.</summary>
/// <param name="Orca">Sends prompts and polls <c>orca worktree ps</c>.</param>
/// <param name="Store">The state files.</param>
/// <param name="Clock">Time.</param>
/// <param name="MaxInFlight">Asks outstanding at once.</param>
/// <param name="Wait">How long to wait for each answer.</param>
/// <param name="NewAskId">Makes the id each prompt carries.</param>
/// <param name="RigExecutable">The <c>rig</c> the prompt tells the agent to run.</param>
public sealed record WrapupRolling(WrapupOrca Orca, IWrapupStore Store, IWrapupClock Clock, int MaxInFlight, TimeSpan Wait, Func<string> NewAskId, string RigExecutable);

/// <summary>The background follower <c>estate wrapup --apply</c> starts: one at a time, guarded by a lock file.</summary>
public interface IWrapupFollower
{
    /// <summary>Where followers log.</summary>
    string LogDir { get; }

    /// <summary>Whether a follower holds the lock now.</summary>
    bool Running();

    /// <summary>Holds the lock until disposed; null when another follower holds it.</summary>
    IDisposable? TryHold();

    /// <summary>Starts <c>estate wrapup <paramref name="args"/> --follow</c> detached; returns its log file.</summary>
    Task<string> StartAsync(IReadOnlyList<string> args, DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>The real follower: <c>&lt;app data&gt;/wrapup/follow.lock</c> and <c>&lt;app data&gt;/logs/wrapup/</c>.</summary>
/// <param name="appData">rig's app data folder.</param>
/// <param name="detached">Starts the follower.</param>
/// <param name="self">The command line that runs this rig, before <c>estate wrapup</c>.</param>
public sealed class WrapupFollower(string appData, IDetachedStart detached, IReadOnlyList<string> self) : IWrapupFollower
{
    /// <summary>The lock file.</summary>
    public string LockPath => Path.Join(appData, "wrapup", "follow.lock");

    /// <inheritdoc />
    public string LogDir => Path.Join(appData, "logs", "wrapup");

    /// <inheritdoc />
    public bool Running()
    {
        using var held = TryHold();
        return held is null;
    }

    /// <inheritdoc />
    public IDisposable? TryHold()
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        try
        {
            // FileShare.None is flock(LOCK_EX | LOCK_NB) on Unix and a share-deny open on Windows.
            var handle = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (handle.Length == 0)
            {
                handle.WriteByte((byte)'0');
                handle.Flush();
            }

            return handle;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string> StartAsync(IReadOnlyList<string> args, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        _ = Directory.CreateDirectory(LogDir);
        var log = Path.Join(LogDir, $"follow-{now.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}.log");
        _ = await detached.StartAsync([.. self, "estate", "wrapup", .. args, "--follow"], log, cancellationToken: cancellationToken).ConfigureAwait(false);
        return log;
    }
}

/// <summary>A follower that never starts: the default wherever no real one is wired, so nothing spawns by accident.</summary>
public sealed class NoWrapupFollower : IWrapupFollower
{
    /// <inheritdoc />
    public string LogDir => "";

    /// <inheritdoc />
    public bool Running() => false;

    /// <inheritdoc />
    public IDisposable? TryHold() => null;

    /// <inheritdoc />
    public Task<string> StartAsync(IReadOnlyList<string> args, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("no wrap-up follower is configured");
}
