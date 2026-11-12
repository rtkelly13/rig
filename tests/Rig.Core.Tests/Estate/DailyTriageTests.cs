using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="DailyTriage"/> over scripted origins and a GraphQL seam that answers per repository.</summary>
public sealed class DailyTriageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 30, 0, TimeSpan.Zero);

    private readonly TempDir _root = new();

    private readonly FakeProc _proc = new();

    private readonly PerRepo _github = new();

    public void Dispose() => _root.Dispose();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Clone(string relative, string remote)
    {
        var path = _root.Folder(relative);
        _ = _proc.AnswerIn(path, new Ran(0, remote + "\n", string.Empty), "git");
        return path;
    }

    private Task<DailyTriageReport> ScanAsync(params string[] clones) =>
        new DailyTriage(new Git(_proc, _root.Path), _github, new ManualClock(Now)).ScanAsync(clones, ["rtkelly13", "rtkelly-labs"], cancellationToken: Token);

    [Fact]
    public async Task Owned_repos_are_asked_once_and_every_other_clone_is_report_only()
    {
        var rig = Clone("personal/rig", "git@github.com:rtkelly13/rig.git");
        var worktree = Clone("personal/rig-wt", "https://github.com/RTKELLY13/rig");
        var labs = Clone("personal/lake", "git@github.com:rtkelly-labs/Lake.NET.git");
        var work = Clone("work/service", "git@github.com:someone-else/service.git");
        var local = Clone("personal/scratch", string.Empty);
        _github.Repos["rtkelly13/rig"] = [Pull(1, state: "CLEAN")];
        _github.Repos["rtkelly-labs/Lake.NET"] = [];

        var report = await ScanAsync(rig, worktree, labs, work, local);

        Assert.Equal(["rtkelly13/rig", "rtkelly-labs/Lake.NET"], report.Repos.Select(r => r.Repo));
        Assert.Equal([1, 0], report.Repos.Select(r => r.OpenPulls));
        Assert.Equal([work, local], report.ReportOnly.Select(c => c.Path));
        Assert.Equal("someone-else/service", report.ReportOnly[0].Repo);
        Assert.Equal(2, _github.Calls.Count);
        var pull = Assert.Single(report.Pulls);
        Assert.Equal("pr:rtkelly13/rig#1", pull.Id);
        Assert.Equal(["ready"], pull.Signals);
        Assert.Equal(TriageLane.Owner, pull.Lane);
    }

    [Fact]
    public async Task Broken_pulls_are_work_and_their_checks_and_threads_are_named()
    {
        var rig = Clone("personal/rig", "git@github.com:rtkelly13/rig.git");
        _github.Repos["rtkelly13/rig"] =
        [
            Pull(
                1,
                state: "BEHIND",
                checks: [Run("lint", "COMPLETED", "FAILURE"), Run("test", "IN_PROGRESS", null), Status("ci/legacy", "ERROR"), Run("ok", "COMPLETED", "SUCCESS")],
                threads: [false, true, false]),
            Pull(2, state: "DIRTY", mergeable: "CONFLICTING"),
        ];

        var report = await ScanAsync(rig);

        var first = report.Pulls[0];
        Assert.Equal(["failing_checks", "behind", "unresolved_threads", "pending_checks"], first.Signals);
        Assert.Equal(["lint", "ci/legacy"], first.FailingChecks);
        Assert.Equal(["test"], first.PendingChecks);
        Assert.Equal(2, first.UnresolvedThreads);
        Assert.Equal(TriageLane.Work, first.Lane);
        Assert.Equal(["conflict"], report.Pulls[1].Signals);
        Assert.Equal(TriageLane.Work, report.Pulls[1].Lane);
    }

    [Fact]
    public async Task Draft_stale_and_superseded_pulls_go_to_the_owner_even_when_broken()
    {
        var rig = Clone("personal/rig", "git@github.com:rtkelly13/rig.git");
        _github.Repos["rtkelly13/rig"] =
        [
            Pull(3, state: "BEHIND", closes: [10]),
            Pull(4, state: "BEHIND", closes: [10, 11]),
            Pull(5, state: "BEHIND", draft: true),
            Pull(6, state: "BEHIND", updated: Now.AddDays(-30)),
            Pull(7, state: "BLOCKED"),
        ];

        var report = await ScanAsync(rig);

        var byNumber = report.Pulls.ToDictionary(p => p.Number);
        Assert.Equal(4, byNumber[3].SupersededBy);
        Assert.Equal(["behind", "superseded"], byNumber[3].Signals);
        Assert.Null(byNumber[4].SupersededBy);
        Assert.Equal(TriageLane.Work, byNumber[4].Lane);
        Assert.Equal(["behind", "draft"], byNumber[5].Signals);
        Assert.Equal(["behind", "stale"], byNumber[6].Signals);
        Assert.Equal(["blocked"], byNumber[7].Signals);
        Assert.All([byNumber[3], byNumber[5], byNumber[6], byNumber[7]], p => Assert.Equal(TriageLane.Owner, p.Lane));
    }

    [Fact]
    public async Task Pending_checks_or_an_unknown_merge_state_wait_for_the_next_run()
    {
        var rig = Clone("personal/rig", "git@github.com:rtkelly13/rig.git");
        _github.Repos["rtkelly13/rig"] =
        [
            Pull(1, state: "UNKNOWN"),
            Pull(2, state: "UNSTABLE", checks: [Run("test", "QUEUED", null)]),
        ];

        var report = await ScanAsync(rig);

        Assert.Equal(["merge_state_unknown"], report.Pulls[0].Signals);
        Assert.Equal(["pending_checks"], report.Pulls[1].Signals);
        Assert.All(report.Pulls, p => Assert.Equal(TriageLane.Wait, p.Lane));
    }

    [Fact]
    public async Task An_untrusted_clone_is_never_work()
    {
        var fork = Clone("external/fork", "git@github.com:rtkelly13/fork.git");
        _github.Repos["rtkelly13/fork"] = [Pull(1, state: "BEHIND")];

        var report = await ScanAsync(fork);

        Assert.False(Assert.Single(report.Repos).Trusted);
        Assert.Equal(TriageLane.Owner, Assert.Single(report.Pulls).Lane);
    }

    [Fact]
    public async Task A_repo_github_cannot_answer_carries_its_error_and_the_scan_goes_on()
    {
        var gone = Clone("personal/gone", "git@github.com:rtkelly13/gone.git");
        var rig = Clone("personal/rig", "git@github.com:rtkelly13/rig.git");
        _github.Repos["rtkelly13/rig"] = [Pull(1, state: "CLEAN")];

        var report = await ScanAsync(gone, rig);

        Assert.Equal("gh: Could not resolve to a Repository", report.Repos[0].Error);
        Assert.Null(report.Repos[0].OpenPulls);
        Assert.Null(report.Repos[1].Error);
        Assert.Single(report.Pulls);
    }

    private static JsonObject Pull(
        int number,
        string state,
        string mergeable = "MERGEABLE",
        bool draft = false,
        DateTimeOffset? updated = null,
        JsonObject[]? checks = null,
        bool[]? threads = null,
        int[]? closes = null) => new()
        {
            ["number"] = number,
            ["title"] = $"PR {number}",
            ["url"] = $"https://github.com/x/y/pull/{number}",
            ["isDraft"] = draft,
            ["headRefName"] = $"branch-{number}",
            ["baseRefName"] = "main",
            ["mergeable"] = mergeable,
            ["mergeStateStatus"] = state,
            ["updatedAt"] = (updated ?? Now.AddDays(-1)).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["author"] = new JsonObject { ["login"] = "rtkelly13" },
            ["reviewThreads"] = Connection((threads ?? []).Select(resolved => new JsonObject { ["isResolved"] = resolved })),
            ["closingIssuesReferences"] = Connection((closes ?? []).Select(issue => new JsonObject { ["number"] = issue })),
            ["commits"] = Connection(
            [
                new JsonObject
                {
                    ["commit"] = new JsonObject
                    {
                        ["statusCheckRollup"] = checks is null ? null : new JsonObject { ["contexts"] = Connection(checks) },
                    },
                },
            ]),
        };

    private static JsonObject Run(string name, string status, string? conclusion) =>
        new() { ["__typename"] = "CheckRun", ["name"] = name, ["status"] = status, ["conclusion"] = conclusion };

    private static JsonObject Status(string context, string state) =>
        new() { ["__typename"] = "StatusContext", ["context"] = context, ["state"] = state };

    private static JsonObject Connection(IEnumerable<JsonObject> nodes) => new() { ["nodes"] = new JsonArray([.. nodes]) };

    /// <summary>Answers the open-PR query with the PRs held for the repository its variables name.</summary>
    private sealed class PerRepo : IGitHubApi
    {
        public Dictionary<string, JsonObject[]> Repos { get; } = [];

        public List<string> Calls { get; } = [];

        public Task<Ran> CallAsync(ApiRequest request, CancellationToken cancellationToken = default)
        {
            var variables = JsonNode.Parse(request.Body!)!["variables"]!;
            var repo = $"{variables["owner"]!.GetValue<string>()}/{variables["name"]!.GetValue<string>()}";
            Calls.Add(repo);
            if (!Repos.TryGetValue(repo, out var pulls))
            {
                return Task.FromResult(new Ran(1, string.Empty, "gh: Could not resolve to a Repository\n"));
            }

            var data = new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["repository"] = new JsonObject { ["pullRequests"] = Connection(pulls.Select(p => (JsonObject)p.DeepClone())) },
                },
            };
            return Task.FromResult(new Ran(0, data.ToJsonString(), string.Empty));
        }
    }
}
