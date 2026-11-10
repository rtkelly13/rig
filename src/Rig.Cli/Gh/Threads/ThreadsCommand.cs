using System.CommandLine;
using System.Text.Encodings.Web;
using System.Text.Json;
using Rig.Core;
using Rig.Core.GitHub;

namespace Rig.Cli.Gh.Threads;

/// <summary>Reads the GitHub repo the working directory's <c>origin</c> points at, or null when there is none.</summary>
internal delegate Task<RepoSlug?> OriginReader(CancellationToken cancellationToken);

/// <summary>What <c>resolve</c> did, or with <c>--dry-run</c> would do.</summary>
internal sealed record ResolveOutcome(string ThreadId, string Sha, string? ReplyUrl, bool Replied, bool Resolved, bool DryRun);

/// <summary>
/// <c>rig gh threads list|resolve</c>: TEMPORARY, retire once <c>gh</c> handles PR review threads natively. Exit codes
/// follow rig's convention: 0 clean (or done), 1 drift (unresolved threads listed) or a refused resolve, 2 an error
/// talking to GitHub. Output is plain text: agents read it, and summaries hold <c>[P1]</c>, which markup would eat.
/// </summary>
internal sealed class ThreadsCommand(ThreadsGitHub github, OriginReader origin, TextWriter output, TextWriter error)
{
    private const string Retire = "TEMPORARY: retire once `gh` can list, reply to and resolve PR review threads itself.";

    /// <summary>The JSON <c>--json</c> prints: snake_case fields, two-space indent, text unescaped where JSON allows.</summary>
    internal static readonly JsonSerializerOptions Printed = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Compact = new(Printed) { WriteIndented = false };

    /// <summary>The <c>rig gh</c> command group.</summary>
    public Command Build()
    {
        var gh = new Command("gh", $"Fill gaps in the GitHub CLI. {Retire}");
        var threads = new Command(
            "threads",
            $"List, reply to and resolve pull-request review threads. {Retire} "
            + "A thread is resolved only after its fix is pushed, with a reply naming the fix commit.");
        gh.Subcommands.Add(threads);
        threads.Subcommands.Add(BuildList());
        threads.Subcommands.Add(BuildResolve());
        return gh;
    }

    private static Argument<int> PrArgument() => new("pr") { Description = "Pull request number." };

    private static Option<string?> RepoOption() => new("--repo") { Description = "owner/name; defaults to this clone's origin." };

    private Command BuildList()
    {
        var pr = PrArgument();
        var repo = RepoOption();
        var json = new Option<bool>("--json") { Description = "Print the threads as JSON." };
        var list = new Command("list", "List the PR's unresolved review threads. Exits 1 when there are any, 0 when there are none.")
        {
            pr, repo, json,
        };
        list.SetAction((parsed, ct) => ListAsync(parsed.GetValue(pr), parsed.GetValue(repo), parsed.GetValue(json), ct));
        return list;
    }

    private Command BuildResolve()
    {
        var pr = PrArgument();
        var threadId = new Argument<string>("thread_id") { Description = "Review thread id (PRRT_…), as `list` prints it." };
        var sha = new Option<string>("--sha") { Description = "The pushed commit that fixes the thread (full SHA or prefix).", Required = true };
        var note = new Option<string?>("--note") { Description = "Added to the reply after 'Fixed in <sha>.'" };
        var repo = RepoOption();
        var dryRun = new Option<bool>("--dry-run") { Description = "Check and print the plan; write nothing." };
        var json = new Option<bool>("--json") { Description = "Print the outcome as JSON." };
        var resolve = new Command(
            "resolve",
            "Reply 'Fixed in <sha>.' on a thread, then resolve it. Refuses (exit 1) unless the SHA is one of the PR's own "
            + "commits, so a thread is never resolved against a fix that was not pushed. Safe to rerun: an existing reply is not repeated.")
        {
            pr, threadId, sha, note, repo, dryRun, json,
        };
        resolve.SetAction((parsed, ct) => ResolveAsync(
            new ResolveArgs(
                parsed.GetValue(pr),
                parsed.GetValue(threadId)!,
                parsed.GetValue(sha)!,
                parsed.GetValue(note),
                parsed.GetValue(repo),
                parsed.GetValue(dryRun),
                parsed.GetValue(json)),
            ct));
        return resolve;
    }

    private async Task<int> ListAsync(int pr, string? repo, bool asJson, CancellationToken cancellationToken)
    {
        var slug = await TargetAsync(repo, cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return (int)ExitCode.Error;
        }

        IReadOnlyList<ObservedThread> observed;
        try
        {
            observed = await github.FetchThreadsAsync(slug, pr, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubException problem)
        {
            await error.WriteLineAsync(problem.Message).ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        var open = observed.Select(item => item.Thread).Where(thread => !thread.Resolved).ToList();
        if (asJson)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(open.Select(PrintedThread.Of), Printed)).ConfigureAwait(false);
        }
        else
        {
            await PrintThreadsAsync(slug, pr, open).ConfigureAwait(false);
        }

        return (int)ExitCodes.ForPlan(open.Count);
    }

    private async Task<int> ResolveAsync(ResolveArgs args, CancellationToken cancellationToken)
    {
        var slug = await TargetAsync(args.Repo, cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return (int)ExitCode.Error;
        }

        ResolveDecision decision;
        try
        {
            var threads = await github.FetchThreadsAsync(slug, args.Pr, cancellationToken).ConfigureAwait(false);
            var pull = await github.FetchPullRequestAsync(slug, args.Pr, cancellationToken).ConfigureAwait(false);
            decision = ThreadModel.PlanResolve(args.ThreadId, args.Sha, args.Note, threads, pull);
        }
        catch (GitHubException problem)
        {
            await error.WriteLineAsync(problem.Message).ConfigureAwait(false);
            return (int)ExitCode.Error;
        }

        if (decision is not ResolvePlan plan)
        {
            await error.WriteLineAsync($"Refused: {((Refusal)decision).Reason}").ConfigureAwait(false);
            return (int)ExitCode.Drift;
        }

        var outcome = await ApplyAsync(plan, args.DryRun, args.Json, cancellationToken).ConfigureAwait(false);
        if (outcome is null)
        {
            return (int)ExitCode.Error;
        }

        if (args.Json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(outcome, Printed)).ConfigureAwait(false);
        }
        else
        {
            await output.WriteLineAsync(Describe(plan, outcome)).ConfigureAwait(false);
        }

        return (int)ExitCode.Clean;
    }

    /// <summary>Applies the plan, or returns what a dry run would report. Null after a reported GitHub error.</summary>
    private async Task<ResolveOutcome?> ApplyAsync(ResolvePlan plan, bool dryRun, bool asJson, CancellationToken cancellationToken)
    {
        var threadId = plan.Thread.Id;
        if (dryRun || plan.IsEmpty)
        {
            return new ResolveOutcome(threadId, plan.Sha, null, Replied: false, Resolved: plan.Thread.Resolved, DryRun: dryRun);
        }

        string? url = null;
        try
        {
            if (plan.Reply is not null)
            {
                url = await github.ReplyAsync(threadId, plan.Reply, cancellationToken).ConfigureAwait(false);
            }

            if (plan.Resolve)
            {
                await github.ResolveAsync(threadId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (GitHubException problem)
        {
            if (url is not null)
            {
                await error.WriteLineAsync($"Replied ({url}) but did not resolve. Rerun to resolve; the reply is not repeated.")
                    .ConfigureAwait(false);
            }

            if (asJson)
            {
                var partial = new ResolveOutcome(threadId, plan.Sha, url, url is not null, Resolved: false, DryRun: false);
                await output.WriteLineAsync(JsonSerializer.Serialize(partial, Compact)).ConfigureAwait(false);
            }

            await error.WriteLineAsync(problem.Message).ConfigureAwait(false);
            return null;
        }

        return new ResolveOutcome(threadId, plan.Sha, url, Replied: url is not null, Resolved: true, DryRun: false);
    }

    private async Task<RepoSlug?> TargetAsync(string? repo, CancellationToken cancellationToken)
    {
        var slug = repo is not null ? RepoSlug.FromPath(repo) : await origin(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            await error.WriteLineAsync(
                repo is not null
                    ? $"Cannot tell which repo: pass --repo owner/name ({PyRepr.Of(repo)} is not one)"
                    : "Cannot tell which repo: no GitHub origin here; pass --repo owner/name").ConfigureAwait(false);
        }

        return slug;
    }

    private async Task PrintThreadsAsync(RepoSlug slug, int pr, List<ReviewThread> threads)
    {
        if (threads.Count == 0)
        {
            await output.WriteLineAsync($"{slug}#{pr}: no unresolved review threads").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"{slug}#{pr}: {threads.Count} unresolved review thread(s)").ConfigureAwait(false);
        foreach (var thread in threads)
        {
            var outdated = thread.Outdated ? " (outdated)" : string.Empty;
            await output.WriteLineAsync($"{thread.Id}  {thread.Location}{outdated}  @{thread.Author}").ConfigureAwait(false);
            await output.WriteLineAsync($"    {thread.Summary}").ConfigureAwait(false);
        }
    }

    private static string Describe(ResolvePlan plan, ResolveOutcome outcome)
    {
        var where = $"{plan.Thread.Id} ({plan.Thread.Location})";
        if (outcome.DryRun)
        {
            var reply = plan.Reply is not null ? $"reply {PyRepr.Of(plan.Reply)}" : "skip the reply (already posted)";
            var resolve = plan.Resolve ? "resolve" : "skip resolving (already resolved)";
            return $"Dry run, {where}: would {reply} then {resolve}.";
        }

        var fix = ThreadModel.Prefix(plan.Sha, 12);
        if (plan.IsEmpty)
        {
            return $"{where}: already replied with {fix} and resolved; nothing to do.";
        }

        var replied = outcome.Replied ? $"replied {outcome.ReplyUrl}" : "reply already posted";
        var resolved = plan.Resolve ? "resolved" : "was already resolved";
        return $"{where}: {replied}; {resolved} (fix {fix}).";
    }

    private sealed record ResolveArgs(int Pr, string ThreadId, string Sha, string? Note, string? Repo, bool DryRun, bool Json);

    /// <summary>A thread as <c>list --json</c> prints it: the fields in Python's order, without the derived location.</summary>
    private sealed record PrintedThread(
        string Id, string Path, int? Line, bool Outdated, bool Resolved, string Author, string Summary, string Url, string Body)
    {
        public static PrintedThread Of(ReviewThread t) =>
            new(t.Id, t.Path, t.Line, t.Outdated, t.Resolved, t.Author, t.Summary, t.Url, t.Body);
    }
}
