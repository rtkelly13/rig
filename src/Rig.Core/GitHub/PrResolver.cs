using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Core.GitHub;

/// <summary>
/// Pull-request state and merge chains from GitHub's GraphQL API, through <see cref="IGitHubApi"/>. Ported from
/// <c>rig.github_prs</c>.
/// </summary>
/// <remarks>
/// Per repository, one query lists the open PRs and looks each requested PR up by alias. Nothing throws for a GitHub
/// problem: a PR that cannot be resolved comes back <see cref="PrStatus.Unknown"/> with the reason.
/// </remarks>
public static class PrResolver
{
    /// <summary>Pages of 100 open PRs read before a repository is given up on.</summary>
    public const int MaxOpenPages = 10;

    /// <summary>PR lookups per query.</summary>
    public const int LookupsPerQuery = 100;

    /// <summary>Repositories queried at once.</summary>
    public const int MaxWorkers = 8;

    private const string OpenFields =
        "pageInfo { hasNextPage endCursor } nodes { number headRefName baseRefName createdAt isCrossRepository "
        + "mergeQueueEntry { position } labels(first: 20) { nodes { name } } "
        + "timelineItems(itemTypes: [LABELED_EVENT], last: 25) "
        + "{ nodes { ... on LabeledEvent { createdAt label { name } } } } }";

    private const string LookupFields =
        "number state merged baseRefName headRefName additions deletions changedFiles isDraft reviewDecision "
        + "viewerDidAuthor reviewRequests(first: 20) { nodes { requestedReviewer { ... on User { isViewer } } } } "
        + "commits(last: 1) { nodes { commit { statusCheckRollup { state } } } }";

    /// <summary>
    /// The query for one repository: the open-PR page when <paramref name="openList"/>, and a <c>pr&lt;n&gt;</c> lookup per
    /// number. Owner, name and cursor travel as variables, so nothing in them can shape the document.
    /// </summary>
    public static ApiRequest BuildQuery(string owner, string repo, IEnumerable<int> numbers, bool openList, string? after = null)
    {
        var parts = new List<string>();
        if (openList)
        {
            parts.Add($"open: pullRequests(states: OPEN, first: 100, after: $after) {{ {OpenFields} }}");
        }

        parts.AddRange(numbers.Select(number => string.Create(
            CultureInfo.InvariantCulture, $"pr{number}: pullRequest(number: {number}) {{ {LookupFields} }}")));
        var declared = openList ? "$owner: String!, $name: String!, $after: String" : "$owner: String!, $name: String!";
        var query = $"query({declared}) {{ repository(owner: $owner, name: $name) {{ {string.Join(' ', parts)} }} }}";
        var variables = new Dictionary<string, object?>
        {
            ["owner"] = owner,
            ["name"] = repo,
            ["after"] = openList && !string.IsNullOrEmpty(after) ? after : null,
        };
        return ApiRequest.GraphQL(query, variables);
    }

    /// <summary>States for <paramref name="refs"/>, and every chain that includes one. Repositories are queried in parallel.</summary>
    public static async Task<Resolution> ResolveAsync(
        IGitHubApi github,
        IEnumerable<PrRef> refs,
        int lookupsPerQuery = LookupsPerQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(refs);
        var started = Stopwatch.GetTimestamp();
        var byRepo = refs.GroupBy(pr => (pr.Owner, pr.Repo))
            .OrderBy(group => group.Key.Owner, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Repo, StringComparer.Ordinal)
            .Select(group => (group.Key.Owner, group.Key.Repo, Numbers: group.Select(pr => pr.Number).ToList()))
            .ToList();
        using var gate = new SemaphoreSlim(MaxWorkers);
        var resolved = await Task.WhenAll(byRepo.Select(async repo =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ResolveRepoAsync(github, repo.Owner, repo.Repo, repo.Numbers, lookupsPerQuery, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = gate.Release();
            }
        })).ConfigureAwait(false);

        var states = new Dictionary<PrRef, PrState>();
        var chains = new List<RepoChain>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (repo, result) in byRepo.Zip(resolved))
        {
            if (result.Error.Length > 0)
            {
                failed[$"{repo.Owner}/{repo.Repo}"] = result.Error;
            }

            foreach (var (number, state) in result.States.OrderBy(pair => pair.Key))
            {
                states[new PrRef(repo.Owner, repo.Repo, number)] = state;
            }

            var wanted = repo.Numbers.ToHashSet();
            chains.AddRange(result.Chains.Where(chain => chain.Members.Any(wanted.Contains)).Select(chain => RepoChain.Of(repo.Owner, repo.Repo, chain)));
        }

        return new Resolution(states, chains, failed, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    /// <summary>Just the state of each PR in <paramref name="refs"/>.</summary>
    public static async Task<IReadOnlyDictionary<PrRef, PrState>> ResolveStatesAsync(
        IGitHubApi github, IEnumerable<PrRef> refs, CancellationToken cancellationToken = default) =>
        (await ResolveAsync(github, refs, cancellationToken: cancellationToken).ConfigureAwait(false)).States;

    /// <summary>States for <paramref name="numbers"/> and the repository's chains, built from its full open-PR list.</summary>
    public static async Task<RepoResolution> ResolveRepoAsync(
        IGitHubApi github,
        string owner,
        string repo,
        IEnumerable<int> numbers,
        int lookupsPerQuery = LookupsPerQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(github);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lookupsPerQuery);
        var wanted = numbers.Distinct().Order().ToList();
        List<int[]> chunks = wanted.Count > 0 ? [.. wanted.Chunk(lookupsPerQuery)] : [[]];
        var states = new Dictionary<int, PrState>();
        var openPrs = new List<OpenPr>();
        try
        {
            string? cursor = null;
            var complete = false;
            for (var page = 0; page < MaxOpenPages && !complete; page++)
            {
                int[] lookups = page == 0 ? chunks[0] : [];
                var answer = await RunAsync(github, BuildQuery(owner, repo, lookups, openList: true, cursor), cancellationToken).ConfigureAwait(false);
                foreach (var number in lookups)
                {
                    states[number] = LookupState(answer, number);
                }

                var listing = OpenList(answer);
                openPrs.AddRange(listing.Prs);
                complete = !listing.HasNextPage;
                cursor = listing.EndCursor ?? string.Empty;
            }

            if (!complete)
            {
                throw new GhException(string.Create(CultureInfo.InvariantCulture, $"more than {MaxOpenPages * 100} open PRs"));
            }

            foreach (var chunk in chunks.Skip(1))
            {
                var answer = await RunAsync(github, BuildQuery(owner, repo, chunk, openList: false), cancellationToken).ConfigureAwait(false);
                foreach (var number in chunk)
                {
                    states[number] = LookupState(answer, number);
                }
            }
        }
        catch (GhException error)
        {
            // Chains need the complete open list, so a partial answer yields none.
            var partial = wanted.ToDictionary(number => number, number => states.GetValueOrDefault(number) ?? new PrState(PrStatus.Unknown, error.Message));
            return new RepoResolution(partial, [], error.Message);
        }

        return new RepoResolution(states, PrChains.Build(openPrs));
    }

    /// <summary>One node of the open-PR listing. Throws <see cref="FormatException"/> when it is misshapen.</summary>
    public static OpenPr ParseOpenPr(JsonNode? node) => JsonShape.Read(() => OpenPrFrom(JsonShape.Object(node)));

    /// <summary>What a PR lookup says about size, review and checks. Every field is optional; a misshapen one reads as the defaults.</summary>
    public static PrDetails ParseDetails(JsonNode? node) => JsonShape.TryRead(() => DetailsFrom(JsonShape.Object(node))) ?? new PrDetails();

    private static async Task<Answer> RunAsync(IGitHubApi github, ApiRequest request, CancellationToken cancellationToken)
    {
        // gh exits 1 when any alias fails (a PR number that does not exist) but still prints the data.
        var ran = await github.CallAsync(request, cancellationToken).ConfigureAwait(false);
        var body = ran.Out.Trim().Length > 0 ? ParseBody(ran.Out) : null;
        var aliasErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        var messages = new List<string>();
        foreach (var (path, message) in body?.Errors ?? [])
        {
            if (path.Count >= 2)
            {
                aliasErrors[path[1]] = message;
            }
            else
            {
                messages.Add(message);
            }
        }

        if (body?.Repository is not { } repository)
        {
            var detail = messages.Count > 0 ? string.Join("; ", messages)
                : ran.Err.Trim() is { Length: > 0 } err ? err
                : string.Create(CultureInfo.InvariantCulture, $"gh exited {ran.Code}");
            throw new GhException(detail);
        }

        return new Answer(repository, aliasErrors);
    }

    // The reply's repository object and errors; null when the whole body is misshapen, as the Python's lenient decode.
    private static Body? ParseBody(string text)
    {
        try
        {
            return JsonShape.TryRead(() =>
            {
                var root = JsonShape.Object(JsonNode.Parse(text));
                var data = JsonShape.OptionalObject(root, "data");
                var repository = data is null ? null : JsonShape.OptionalObject(data, "repository");
                var errors = root["errors"] is null ? [] : JsonShape.Items(root, "errors").Select(Error).ToList();
                return new Body(repository, errors);
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (IReadOnlyList<string> Path, string Message) Error(JsonObject error)
    {
        var path = JsonShape.Scalars(error, "path");
        var message = JsonShape.OptionalString(error, "message") is { Length: > 0 } text ? text
            : JsonShape.OptionalString(error, "type") is { Length: > 0 } kind ? kind
            : "error";
        return (path, message);
    }

    private static PrState LookupState(Answer answer, int number)
    {
        var alias = string.Create(CultureInfo.InvariantCulture, $"pr{number}");
        var lookup = JsonShape.TryRead(() => Lookup.From(JsonShape.Object(answer.Repository[alias])));
        if (lookup is null)
        {
            return new PrState(PrStatus.Unknown, answer.AliasErrors.GetValueOrDefault(alias) ?? "not found");
        }

        var details = lookup.Details;
        if (lookup.Merged == true)
        {
            return new PrState(PrStatus.Merged, Details: details);
        }

        return lookup.State switch
        {
            "OPEN" => new PrState(PrStatus.Open, Details: details),
            "MERGED" => new PrState(PrStatus.Merged, Details: details),
            "CLOSED" => new PrState(PrStatus.Closed, Details: details),
            var state => new PrState(PrStatus.Unknown, $"unexpected state {Vault.PyText.Repr(state ?? string.Empty)}"),
        };
    }

    private static Listing OpenList(Answer answer)
    {
        var listing = JsonShape.TryRead(() =>
        {
            var open = JsonShape.OptionalObject(answer.Repository, "open");
            if (open is null)
            {
                return new Listing([], false, null);
            }

            var info = JsonShape.DefaultedObject(open, "pageInfo");
            var prs = JsonShape.Items(open, "nodes").Select(OpenPrFrom).ToList();
            return new Listing(
                prs,
                info is not null && JsonShape.Bool(info, "hasNextPage"),
                info is null ? null : JsonShape.OptionalString(info, "endCursor"));
        });

        // Chains built from a partial list would be wrong, so this fails the repository.
        return listing ?? throw new GhException("unreadable open-PR list");
    }

    private static OpenPr OpenPrFrom(JsonObject node)
    {
        var labels = JsonShape.OptionalObject(node, "labels") is { } labelNodes
            ? JsonShape.Items(labelNodes, "nodes").Select(label => JsonShape.String(label, "name").ToLowerInvariant()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var labeledAt = new Dictionary<string, string>(StringComparer.Ordinal);
        var events = JsonShape.OptionalObject(node, "timelineItems") is { } timeline ? JsonShape.Items(timeline, "nodes") : [];
        foreach (var labelEvent in events)
        {
            var label = JsonShape.OptionalObject(labelEvent, "label");
            var name = label is null ? string.Empty : JsonShape.String(label, "name").ToLowerInvariant();
            var when = JsonShape.String(labelEvent, "createdAt");
            if (name.Length > 0 && string.CompareOrdinal(when, labeledAt.GetValueOrDefault(name) ?? string.Empty) > 0)
            {
                labeledAt[name] = when;
            }
        }

        var queue = JsonShape.OptionalObject(node, "mergeQueueEntry");
        return new OpenPr(
            JsonShape.Int(node, "number"),
            JsonShape.String(node, "headRefName"),
            JsonShape.String(node, "baseRefName"),
            JsonShape.String(node, "createdAt"),
            JsonShape.Bool(node, "isCrossRepository"),
            queue is null ? null : JsonShape.OptionalInt(queue, "position"))
        {
            Labels = labels,
            LabeledAt = labeledAt,
        };
    }

    private static PrDetails DetailsFrom(JsonObject node) => Lookup.From(node).Details;

    private sealed record Body(JsonObject? Repository, IReadOnlyList<(IReadOnlyList<string> Path, string Message)> Errors);

    private sealed record Answer(JsonObject Repository, IReadOnlyDictionary<string, string> AliasErrors);

    private sealed record Listing(IReadOnlyList<OpenPr> Prs, bool HasNextPage, string? EndCursor);

    private sealed record Lookup(string? State, bool? Merged, PrDetails Details)
    {
        public static Lookup From(JsonObject node)
        {
            var requested = JsonShape.OptionalObject(node, "reviewRequests") is { } requests
                && JsonShape.Items(requests, "nodes").Any(request =>
                    JsonShape.OptionalObject(request, "requestedReviewer") is { } reviewer && JsonShape.Bool(reviewer, "isViewer"));
            var head = JsonShape.OptionalObject(node, "commits") is { } commits ? JsonShape.ItemsWithNulls(commits, "nodes") is { Count: > 0 } heads ? heads[0] : null : null;
            var commit = head is null ? null : JsonShape.OptionalObject(head, "commit");
            var rollup = commit is null ? null : JsonShape.OptionalObject(commit, "statusCheckRollup");
            var details = new PrDetails(
                JsonShape.OptionalInt(node, "additions") ?? 0,
                JsonShape.OptionalInt(node, "deletions") ?? 0,
                JsonShape.OptionalInt(node, "changedFiles") ?? 0,
                JsonShape.OptionalBool(node, "isDraft") == true,
                JsonShape.OptionalString(node, "reviewDecision") ?? string.Empty,
                (rollup is null ? null : JsonShape.OptionalString(rollup, "state")) ?? string.Empty,
                JsonShape.OptionalBool(node, "viewerDidAuthor") == true,
                requested);
            return new Lookup(JsonShape.OptionalString(node, "state"), JsonShape.OptionalBool(node, "merged"), details);
        }
    }
}

/// <summary>GitHub could not answer for a whole repository; the resolver turns it into unknown states.</summary>
public sealed class GhException : Exception
{
    /// <summary>An unexplained failure.</summary>
    public GhException()
    {
    }

    /// <summary>A failure with <paramref name="message"/>.</summary>
    public GhException(string message)
        : base(message)
    {
    }

    /// <summary>A failure with <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public GhException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
