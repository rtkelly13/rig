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
    private const string ExitCodes = "Exits 0 merged, 1 blocked (someone has to act), 2 GitHub could not be read, 3 timed out still waiting.";

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
        var pr = new Command("pr", "Wait for a pull request to land, and stop with the reason as soon as it cannot.");
        pr.Subcommands.Add(BuildCommand(
            "wait",
            "Wait until the PR merges, or stop as soon as it cannot land without someone acting. Stops at once for a failed "
            + "or cancelled required check, unresolved review threads when the ruleset requires them resolved, requested "
            + "changes, a required review not given, merge conflicts, a draft, or a closed PR. Stops after --settle for a "
            + "merge that stays BLOCKED or BEHIND with no check pending, for auto-merge being off, and for a PR that left "
            + "the merge queue. While waiting it prints what it is waiting on to stderr each time that changes. Polls 5s "
            + $"after a change, growing to 60s while nothing changes. Read-only. {ExitCodes}",
            queue: false));
        pr.Subcommands.Add(BuildCommand(
            "land",
            "Queue a squash auto-merge (gh pr merge <PR> --auto --squash), then wait as `wait` does. Skips the queueing "
            + "when the PR is already merged, has auto-merge on or is in the merge queue. Branch protection still gates "
            + $"the merge. {ExitCodes} Also exits 2 when the merge could not be queued.",
            queue: true));
        return pr;
    }

    private Command BuildCommand(string name, string description, bool queue)
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
            Description = "How long a state with no named cause (BLOCKED or BEHIND with nothing pending, auto-merge off, out of "
                + "the merge queue) must last before the wait stops. GitHub lags between the last check passing and merging.",
            DefaultValueFactory = _ => "30s",
        };
        var json = new Option<bool>("--json") { Description = "Print the final report as one JSON document on stdout." };
        var command = new Command(name, description) { pr, repo, timeout, settle, json };
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
                seen?.PendingChecks ?? [],
                seen?.FailedChecks ?? [],
                [.. (seen?.Threads ?? []).Select(t => new ReportedThread(t.Id, t.Location, t.Author, t.Summary, t.Url))]);
        }
    }
}
