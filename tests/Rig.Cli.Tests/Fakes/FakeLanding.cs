using System.Globalization;
using System.Text.Json.Nodes;
using Rig.Core.Processes;

namespace Rig.Cli.Tests.Fakes;

/// <summary>A check on the fake PR's head commit. <see cref="Result"/> is a conclusion, or <c>PENDING</c> while it runs.</summary>
public sealed record FakeCheck(string Name, string Result = "SUCCESS", bool Required = true, bool CommitStatus = false);

/// <summary>What one poll of the fake PR sees.</summary>
public sealed record FakeLandingState
{
    public string State { get; init; } = "OPEN";

    public bool Draft { get; init; }

    public string Mergeable { get; init; } = "MERGEABLE";

    public string MergeState { get; init; } = "CLEAN";

    public string? ReviewDecision { get; init; }

    public string Base { get; init; } = "main";

    public bool AutoMerge { get; init; } = true;

    public string AutoMergeMethod { get; init; } = "SQUASH";

    public int? QueuePosition { get; init; }

    public IReadOnlyList<FakeCheck> Checks { get; init; } = [new FakeCheck("build")];

    /// <summary>When set, the read fails with this GraphQL error.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// GitHub as <c>rig gh pr wait</c> reads it, behind <see cref="IGitHubApi"/>: the landing query answered from a script
/// (one state per poll; the last one repeats), the base branch's rules from <see cref="RulesJson"/>, and every other
/// GraphQL operation handed to <see cref="Threads"/>.
/// </summary>
public sealed class FakeLanding(FakeGraphQL threads, params FakeLandingState[] script) : IGitHubApi
{
    public FakeGraphQL Threads { get; } = threads;

    /// <summary>The rules REST answers with for <c>main</c>; null answers HTTP 404.</summary>
    public string? RulesJson { get; set; } = "[]";

    /// <summary>The rules of every other base branch.</summary>
    public string OtherRulesJson { get; set; } = "[]";

    public bool ClassicThreadResolution { get; set; }

    public IReadOnlyList<string> ClassicRequiredChecks { get; set; } = [];

    public int PageSize { get; set; } = 100;

    /// <summary>How many times the landing state was read (first pages only).</summary>
    public int Polls { get; private set; }

    public int RulesReads { get; private set; }

    /// <summary>Every landing-query page read.</summary>
    public int LandingCalls { get; private set; }

    public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method == "GET")
        {
            Assert.StartsWith("repos/octo/repo/rules/branches/", request.Route, StringComparison.Ordinal);
            Assert.EndsWith("?per_page=100", request.Route, StringComparison.Ordinal);
            RulesReads++;
            var json = request.Route.Contains("/branches/main?", StringComparison.Ordinal) ? RulesJson : OtherRulesJson;
            return Task.FromResult(json is { } rules ? new Ran(0, rules, string.Empty) : new Ran(1, string.Empty, "gh: Not Found (HTTP 404)"));
        }

        var body = JsonNode.Parse(request.Body!)!.AsObject();
        if (!body["query"]!.GetValue<string>().Contains("query PullRequestLanding", StringComparison.Ordinal))
        {
            return Threads.CallAsync(request, cancellationToken);
        }

        LandingCalls++;
        return Task.FromResult(Landing(body["variables"]!.AsObject()));
    }

    private Ran Landing(JsonObject variables)
    {
        var start = variables["cursor"] is { } cursor ? int.Parse(cursor.GetValue<string>(), CultureInfo.InvariantCulture) : 0;
        if (start == 0)
        {
            Polls++;
        }

        if (variables["number"]!.GetValue<int>() != 7)
        {
            return Ok(new JsonObject { ["repository"] = new JsonObject { ["pullRequest"] = null } });
        }

        var state = script[Math.Min(Polls, script.Length) - 1];
        if (state.Error is { } message)
        {
            var errors = new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["message"] = message }) };
            return new Ran(1, errors.ToJsonString(), $"gh: {message}\n");
        }

        var end = start + PageSize;
        var more = end < state.Checks.Count;
        var contexts = new JsonObject
        {
            ["pageInfo"] = new JsonObject { ["hasNextPage"] = more, ["endCursor"] = more ? end.ToString(CultureInfo.InvariantCulture) : null },
            ["nodes"] = new JsonArray([.. state.Checks.Skip(start).Take(PageSize).Select(CheckNode)]),
        };
        var protection = ClassicThreadResolution || ClassicRequiredChecks.Count > 0
            ? new JsonObject
            {
                ["requiresConversationResolution"] = ClassicThreadResolution,
                ["requiredStatusCheckContexts"] = new JsonArray([.. ClassicRequiredChecks.Select(name => (JsonNode)name)]),
            }
            : null;
        var pr = new JsonObject
        {
            ["state"] = state.State,
            ["isDraft"] = state.Draft,
            ["mergeable"] = state.Mergeable,
            ["mergeStateStatus"] = state.MergeState,
            ["reviewDecision"] = state.ReviewDecision,
            ["url"] = "https://github.com/octo/repo/pull/7",
            ["baseRefName"] = state.Base,
            ["autoMergeRequest"] = state.AutoMerge ? new JsonObject { ["enabledAt"] = "2026-01-01T00:00:00Z", ["mergeMethod"] = state.AutoMergeMethod } : null,
            ["mergeQueueEntry"] = state.QueuePosition is { } position ? new JsonObject { ["position"] = position, ["state"] = "QUEUED" } : null,
            ["baseRef"] = new JsonObject { ["branchProtectionRule"] = protection },
            ["commits"] = new JsonObject
            {
                ["nodes"] = new JsonArray(new JsonObject
                {
                    ["commit"] = new JsonObject { ["statusCheckRollup"] = new JsonObject { ["contexts"] = contexts } },
                }),
            },
        };
        return Ok(new JsonObject { ["repository"] = new JsonObject { ["pullRequest"] = pr } });
    }

    private static JsonNode CheckNode(FakeCheck check)
    {
        var pending = check.Result == "PENDING";
        return check.CommitStatus
            ? new JsonObject
            {
                ["__typename"] = "StatusContext",
                ["context"] = check.Name,
                ["state"] = check.Result,
                ["isRequired"] = check.Required,
            }
            : new JsonObject
            {
                ["__typename"] = "CheckRun",
                ["name"] = check.Name,
                ["status"] = pending ? "IN_PROGRESS" : "COMPLETED",
                ["conclusion"] = pending ? null : check.Result,
                ["isRequired"] = check.Required,
            };
    }

    private static Ran Ok(JsonObject data) => new(0, new JsonObject { ["data"] = data }.ToJsonString(), string.Empty);
}

/// <summary>A clock that only moves when something sleeps on it, so a whole wait runs without waiting.</summary>
public sealed class FakeTime : TimeProvider
{
    private long _ticks;

    /// <summary>Every sleep asked for, in seconds.</summary>
    public List<double> Sleeps { get; } = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    /// <summary>Records the sleep and moves the clock on by it.</summary>
    public Task SleepAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Sleeps.Add(delay.TotalSeconds);
        _ticks += delay.Ticks;
        return Task.CompletedTask;
    }
}
