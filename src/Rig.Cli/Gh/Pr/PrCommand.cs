using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rig.Cli.Gh.Threads;
using Rig.Core.GitHub;
using Rig.Core.Processes;

namespace Rig.Cli.Gh.Pr;

/// <summary>Queues a squash auto-merge on the PR: <c>gh pr merge &lt;n&gt; --auto --squash</c>.</summary>
internal delegate Task<Ran> AutoMerger(RepoSlug repo, int number, CancellationToken cancellationToken);

/// <summary>
/// <c>rig gh pr wait|land</c>: wait for a pull request to land, and stop with the reason as soon as it cannot. Exit
/// codes: 0 merged, 1 blocked (someone has to act), 2 GitHub could not be read or the merge could not be queued, 3 timed
/// out while the PR was still moving. Progress goes to standard error, the final report to standard output.
/// </summary>
internal sealed partial class PrCommand(
    LandingGitHub github, OriginReader origin, AutoMerger merge, LandingWait waiter, TextWriter output, TextWriter error)
{
    /// <summary>The guide both commands point at.</summary>
    public const string Guide = "docs/git/waiting-for-a-pr.md";

    private const string Stops = """
        Stops at once, exit 1, with one of these reasons:
          check_failed              a required check failed or was cancelled
          unresolved_threads        review threads are open and the rules require them resolved; lists them
          changes_requested         a reviewer asked for changes
          review_required           a required review has not been given
          conflicts                 the branch conflicts with its base
          draft, closed             the PR is a draft, or was closed without merging

        Stops, exit 1, once the state has lasted --settle:
          blocked, behind           BLOCKED or BEHIND with no check pending and none of the causes above
          auto_merge_off            auto-merge is off and it is not in the merge queue, even if checks still run
          removed_from_merge_queue  it was in the merge queue and left without merging

        While it waits, stderr gets one line each time what it waits on changes: the pending checks, the
        merge-queue position. It looks again 5s after a change, slowing to every 60s while nothing changes.

        Exit codes:
          0  merged
          1  blocked: someone has to act, and the reason says what
          2  GitHub could not be read{0}
          3  --timeout passed while it was still moving
        """;

    private const string WaitNotes = """
        Use this instead of a loop over `gh pr view` or `gh pr checks --watch`. Those wait for ever on a PR that is
        blocked by anything other than a failed check, such as an unresolved review thread. Read-only.


        """ + Stops + """


        Examples:
          rig gh pr wait 141                           this clone's PR 141
          rig gh pr wait 141 --repo owner/name --json  one JSON report on stdout, for an agent
          rig gh pr wait 141 --timeout 0               look once: can this land as it stands?

        Guide: 
        """ + Guide;

    private const string LandNotes = """
        The one command for "merge this when it is ready". It runs `gh pr merge <PR> --auto --squash`, then waits as
        `rig gh pr wait` does. It skips the queueing when the PR is already merged, has auto-merge on or is in the
        merge queue. Branch protection still gates the merge.


        """ + Stops + """


        Examples:
          rig gh pr land 141
          rig gh pr land 141 --repo owner/name --json

        Guide: 
        """ + Guide;

    /// <summary>The real <see cref="AutoMerger"/>, through <paramref name="proc"/>.</summary>
    public static AutoMerger GhMerge(IProc proc) => (repo, number, cancellationToken) =>
        proc.RunAsync(
            new ProcRequest(["gh", "pr", "merge", number.ToString(CultureInfo.InvariantCulture), "--auto", "--squash", "--repo", repo.FullName])
            {
                Timeout = GhApi.DefaultTimeout,
            },
            cancellationToken);

    /// <summary>Seconds in <c>45m</c>, <c>2h</c> or <c>90s</c>; a bare number is minutes. Null when <paramref name="text"/> is none of those.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = Duration().Match(text);
        if (!found.Success || !int.TryParse(found.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return null;
        }

        return found.Groups["unit"].Value switch
        {
            "s" => TimeSpan.FromSeconds(n),
            "h" => TimeSpan.FromHours(n),
            _ => TimeSpan.FromMinutes(n),
        };
    }

    /// <summary>The <c>rig gh pr</c> command group.</summary>
    public Command Build()
    {
        var pr = new Command("pr", "Land a pull request: wait for the merge, and stop with the reason as soon as it cannot happen.");
        pr.Subcommands.Add(BuildCommand(
            "wait",
            "Wait for the PR to merge, or stop with the reason as soon as it cannot land without someone acting.",
            WaitNotes.Replace("{0}", string.Empty, StringComparison.Ordinal),
            queue: false));
        pr.Subcommands.Add(BuildCommand(
            "land",
            "Queue a squash auto-merge, then wait for the PR to merge or stop with the reason it cannot.",
            LandNotes.Replace("{0}", ", or the merge could not be queued", StringComparison.Ordinal),
            queue: true));
        return pr;
    }

    private NotedCommand BuildCommand(string name, string description, string notes, bool queue)
    {
        var pr = new Argument<int>("pr") { Description = "Pull request number." };
        var repo = new Option<string?>("--repo") { Description = "owner/name; defaults to this clone's origin." };
        var timeout = new Option<string>("--timeout")
        {
            Description = "Give up after this long: 45m, 2h, 90s; a bare number is minutes. 0 looks once and reports.",
            DefaultValueFactory = _ => "60m",
        };
        var settle = new Option<string>("--settle")
        {
            Description = "How long a state GitHub gives no cause for must last before the wait stops; GitHub lags between "
                + "the last check passing and merging.",
            DefaultValueFactory = _ => "30s",
        };
        var json = new Option<bool>("--json") { Description = "Print the final report as one JSON document on stdout." };
        var command = new NotedCommand(name, description, notes) { pr, repo, timeout, settle, json };
        command.SetAction((parsed, ct) => RunAsync(
            new Args(parsed.GetValue(pr), parsed.GetValue(repo), parsed.GetValue(timeout)!, parsed.GetValue(settle)!, parsed.GetValue(json), queue),
            ct));
        return command;
    }

    private async Task<int> RunAsync(Args args, CancellationToken cancellationToken)
    {
        if (ParseDuration(args.Timeout) is not { } timeout || ParseDuration(args.Settle) is not { } settle)
        {
            await error.WriteLineAsync("Expected a duration such as 45m, 2h or 90s for --timeout and --settle.").ConfigureAwait(false);
            return (int)Outcome.Error;
        }

        if (await GhTarget.ResolveAsync(args.Repo, origin, error, cancellationToken).ConfigureAwait(false) is not { } repo)
        {
            return (int)Outcome.Error;
        }

        var label = $"{repo}#{args.Pr.ToString(CultureInfo.InvariantCulture)}";
        Rules? rules = null;
        async Task<Observation> Observe(CancellationToken ct)
        {
            var seen = await github.ObserveAsync(repo, args.Pr, rules, ct).ConfigureAwait(false);
            rules = seen.Rules;
            return seen;
        }

        if (args.Queue && !await QueueAsync(repo, args.Pr, label, Observe, cancellationToken).ConfigureAwait(false))
        {
            return (int)Outcome.Error;
        }

        var result = await waiter.RunAsync(
            Observe,
            new Pace { Timeout = timeout, Settle = settle },
            tick => error.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"{label} [{Clock(tick.Elapsed)}] {tick.Message} (next look in {tick.NextPoll.TotalSeconds:0}s)")),
            cancellationToken).ConfigureAwait(false);
        var report = Report.Of(repo, args.Pr, result);
        if (args.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, ThreadsCommand.Printed)).ConfigureAwait(false);
        }
        else
        {
            await PrintAsync(label, report).ConfigureAwait(false);
        }

        return (int)result.Outcome;
    }

    /// <summary>Queues auto-merge unless the PR already has it, is queued or is not open. False after a reported failure.</summary>
    private async Task<bool> QueueAsync(RepoSlug repo, int pr, string label, Observer observe, CancellationToken cancellationToken)
    {
        Snapshot first;
        try
        {
            first = (await observe(cancellationToken).ConfigureAwait(false)).Snapshot;
        }
        catch (GitHubException problem)
        {
            await error.WriteLineAsync(problem.Message).ConfigureAwait(false);
            return false;
        }

        if (first.State != "OPEN" || first.AutoMerge || first.Queue is not null)
        {
            return true;
        }

        var ran = await merge(repo, pr, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            var err = ran.Err.Trim();
            await error.WriteLineAsync($"Could not queue auto-merge: {(err.Length > 0 ? err : $"gh exited {ran.Code}")}").ConfigureAwait(false);
            return false;
        }

        await error.WriteLineAsync($"{label}: queued squash auto-merge").ConfigureAwait(false);
        return true;
    }

    private async Task PrintAsync(string label, Report report)
    {
        var waited = Clock(TimeSpan.FromSeconds(report.WaitedSeconds));
        if (report.Outcome == "merged")
        {
            await output.WriteLineAsync($"{label}: merged after {waited}").ConfigureAwait(false);
            return;
        }

        if (report.Reason == Reason.GitHubError)
        {
            await error.WriteLineAsync($"{label}: {report.Message}").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"{label}: {report.Outcome.ToUpperInvariant()} ({report.Reason}) after {waited}: {report.Message}")
            .ConfigureAwait(false);
        if (report.Reason != Reason.UnresolvedThreads)
        {
            return;
        }

        foreach (var thread in report.UnresolvedThreads)
        {
            await output.WriteLineAsync($"  {thread.Id}  {thread.Location}  @{thread.Author}").ConfigureAwait(false);
            await output.WriteLineAsync($"      {thread.Summary}").ConfigureAwait(false);
        }

        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"Resolve each once its fix is pushed: rig gh threads resolve {report.Pr} <thread> --sha <fix> --repo {report.Repo}"))
            .ConfigureAwait(false);
    }

    private static string Clock(TimeSpan elapsed) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");

    [GeneratedRegex(@"^\s*(?<n>\d+)\s*(?<unit>[smh]?)\s*$")]
    private static partial Regex Duration();

    private sealed record Args(int Pr, string? Repo, string Timeout, string Settle, bool Json, bool Queue);

    /// <summary>An unresolved thread as the report lists it.</summary>
    private sealed record ReportedThread(string Id, string Location, string Author, string Summary, string Url);

    /// <summary>The final answer, as <c>--json</c> prints it (snake_case).</summary>
    private sealed record Report(
        string Repo,
        int Pr,
        string Url,
        string Outcome,
        string Reason,
        string Message,
        int WaitedSeconds,
        int Polls,
        string MergeState,
        bool AutoMerge,
        int? QueuePosition,
        IReadOnlyList<string> PendingChecks,
        IReadOnlyList<string> FailedChecks,
        IReadOnlyList<ReportedThread> UnresolvedThreads)
    {
        public static Report Of(RepoSlug repo, int number, WaitResult result)
        {
            var seen = result.Last;
            var pr = seen?.Snapshot;
            return new Report(
                repo.FullName,
                number,
                pr?.Url ?? string.Empty,
                result.Outcome.ToString().ToLowerInvariant(),
                result.Reason,
                result.Message,
                (int)result.Waited.TotalSeconds,
                result.Polls,
                pr?.MergeState ?? string.Empty,
                pr?.AutoMerge ?? false,
                pr?.Queue?.Position,
                seen is null ? [] : LandingModel.PendingChecks(seen),
                seen is null ? [] : LandingModel.FailedChecks(seen),
                [.. (seen?.Threads ?? []).Select(t => new ReportedThread(t.Id, t.Location, t.Author, t.Summary, t.Url))]);
        }
    }
}
