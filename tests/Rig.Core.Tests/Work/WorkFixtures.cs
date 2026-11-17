using Rig.Core.Text;
using System.Text.Json.Nodes;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

/// <summary>A clock that never moves, for the collector and the clone cache.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>The Python implementation's exact report, rebuilt as C# values.</summary>
internal static class WorkFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public const string Root = "/code";

    public const string AppPath = "/code/acme/app";

    public const string LibPath = "/code/other/lib";

    public static string Feature => $"{AppPath}/temp/wt-feature";

    public static string Detached => $"{AppPath}/temp/wt-detached";

    public static string Draft => $"{AppPath}/temp/wt-draft";

    public static string Stale => $"{AppPath}/temp/wt-stale";

    public static string Release => $"{LibPath}/temp/wt-release";

    public static string Open => $"{LibPath}/temp/wt-open";

    public static IReadOnlyList<RepoFacts> Repos() =>
    [
        new RepoFacts(AppPath, "acme/app", "main",
        [
            new Checkout(AppPath, "main", IsMainCheckout: true, Dirty: false),
            new Checkout(Feature, "feat/PROJ-1-thing", IsMainCheckout: false, Dirty: true),
            new Checkout(Detached, string.Empty, IsMainCheckout: false, Dirty: null),
            new Checkout(Draft, "feat/PROJ-3-open", IsMainCheckout: false, Dirty: false),
            new Checkout(Stale, "feat/PROJ-5-stale", IsMainCheckout: false, Dirty: false),
        ])
        {
            Branches =
            [
                new BranchRef("main", Now - TimeSpan.FromDays(1), "origin/main", Ahead: 0, Behind: 0),
                new BranchRef(
                    "feat/PROJ-1-thing",
                    new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(1)),
                    "origin/feat/PROJ-1-thing",
                    Ahead: 0,
                    Behind: 2,
                    Unpushed: 4),
                new BranchRef("feat/PROJ-3-open", Now - TimeSpan.FromDays(1)),
                new BranchRef("feat/PROJ-5-stale", Now - TimeSpan.FromDays(40)),
            ],
        },
        new RepoFacts(LibPath, "other/lib", "master",
        [
            new Checkout(LibPath, "master", IsMainCheckout: true, Dirty: false),
            new Checkout(Release, "release/PROJ-2", IsMainCheckout: false, Dirty: false),
            new Checkout(Open, "feat/PROJ-6-open", IsMainCheckout: false, Dirty: false),
        ])
        {
            Branches =
            [
                new BranchRef("master", Now - TimeSpan.FromDays(2), "origin/master", Ahead: 0, Behind: 0),
                new BranchRef("release/PROJ-2", Now - TimeSpan.FromDays(30), "origin/release/PROJ-2", UpstreamGone: true),
                new BranchRef(
                    "feat/PROJ-6-open",
                    Now - TimeSpan.FromDays(1),
                    "origin/feat/PROJ-6-open",
                    Ahead: 1,
                    Behind: 0,
                    Unpushed: 2),
            ],
        },
    ];

    public static IReadOnlyList<AttributedSession> Sessions() =>
    [
        new(
            new SessionFacts(
                "0b1c2d3e-4f50-6172-8394-a5b6c7d8e9f0",
                "Fix hub partitions",
                WorkSessionState.Live,
                Now - TimeSpan.FromMinutes(2),
                Feature,
                Feature,
                14,
                12345),
            new Attribution(AppPath, "feat/PROJ-1-thing")),
        new(
            new SessionFacts(
                "1a2b3c4d-5e6f-7081-92a3-b4c5d6e7f809",
                "Review the merge",
                WorkSessionState.EndedClean,
                Now - TimeSpan.FromHours(2),
                $"{Feature}/src",
                Feature,
                3),
            new Attribution(AppPath, "feat/PROJ-1-thing")),
        new(
            new SessionFacts(
                "2b3c4d5e-6f70-8192-a3b4-c5d6e7f8091a",
                "Detached experiment",
                WorkSessionState.DiedIdle,
                Now - TimeSpan.FromHours(1),
                Detached,
                Detached,
                1),
            new Attribution(AppPath, string.Empty)),
        new(
            new SessionFacts(
                "3c4d5e6f-7081-92a3-b4c5-d6e7f8091a2b",
                "Stray session",
                WorkSessionState.WaitingOnUser,
                Now - TimeSpan.FromMinutes(30),
                Root,
                $"{Root}/scratch",
                2,
                4242),
            new Attribution(null, Reason: "not in a git checkout: /code")),
    ];

    public static IReadOnlyDictionary<BranchKey, PrInfo> Prs() =>
        new Dictionary<BranchKey, PrInfo>
        {
            [new BranchKey(AppPath, "feat/PROJ-1-thing")] = new(
                812,
                PrStatus.Merged,
                "https://github.com/acme/app/pull/812",
                Now - TimeSpan.FromDays(1)),
            [new BranchKey(AppPath, "feat/PROJ-3-open")] = new(
                900,
                PrStatus.Draft,
                "https://github.com/acme/app/pull/900",
                Now - TimeSpan.FromMinutes(30)),
            [new BranchKey(LibPath, "release/PROJ-2")] = new(
                77,
                PrStatus.Closed,
                "https://github.com/other/lib/pull/77",
                Now - TimeSpan.FromDays(40)),
            [new BranchKey(LibPath, "feat/PROJ-6-open")] = new(
                901,
                PrStatus.Open,
                "https://github.com/other/lib/pull/901",
                Now - TimeSpan.FromHours(3)),
        };

    public static JoinInputs Inputs() => new(
        Repos(),
        Sessions(),
        Prs(),
        new Dictionary<string, PrLookup> { [AppPath] = PrLookup.Ok, [LibPath] = PrLookup.Ok });

    public static WorkReport Report()
    {
        var items = WorkJoin.BuildItems(Inputs(), Now, WorkJoin.DefaultStaleAfter);
        return new WorkReport(Now, Root, items)
        {
            Warnings = ["acme/app: gh lookup failed (offline, unauthenticated or no access)"],
        };
    }

    public static JsonObject FixtureJson() =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Work", "work-list.fixture.json")))!.AsObject();
}
