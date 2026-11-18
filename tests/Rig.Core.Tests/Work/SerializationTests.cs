using System.Text.Json.Nodes;
using Rig.Core.Text;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

public sealed class SerializationTests
{
    [Fact]
    public void The_report_matches_the_recorded_report_byte_for_byte()
    {
        var report = WorkFixtures.Report();

        var rendered = WorkJson.Report(report, report.Items);

        Assert.True(
            JsonNode.DeepEquals(WorkFixtures.FixtureJson(), rendered),
            $"C# output differs from the recorded report:\n{WorkJson.Render(report, report.Items)}");
        Assert.Equal(NormaliseFixture(File.ReadAllText(FixturePath())), NormaliseFixture(WorkJson.Render(report, report.Items)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void The_report_matches_the_fixture_for_both_checkout_line_endings(string newline)
    {
        var report = WorkFixtures.Report();
        var fixture = string.Join(newline, File.ReadAllLines(FixturePath())) + newline;

        Assert.Equal(NormaliseFixture(fixture), NormaliseFixture(WorkJson.Render(report, report.Items)));
    }

    [Fact]
    public void The_report_has_exactly_the_documented_keys()
    {
        var report = WorkFixtures.Report();
        var payload = WorkJson.Report(report, report.Items);

        Assert.Equal(["schema_version", "generated_at", "code_root", "warnings", "items"], payload.Select(pair => pair.Key));
        var item = payload["items"]!.AsArray().Single(node => node!["key"]!.GetValue<string>() == "acme/app:feat/PROJ-1-thing")!.AsObject();
        Assert.Equal(
            [
                "key", "repo", "repo_path", "branch", "ticket", "is_trunk", "state", "reasons", "last_activity",
                "dirty", "unpushed", "checkouts", "branch_ref", "pr", "pr_lookup", "sessions",
            ],
            item.Select(pair => pair.Key));
        Assert.Equal(["path", "is_main_checkout", "dirty"], item["checkouts"]!.AsArray()[0]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["upstream", "upstream_gone", "ahead", "behind", "unpushed", "last_commit"],
            item["branch_ref"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(["number", "state", "url", "updated_at"], item["pr"]!.AsObject().Select(pair => pair.Key));
        Assert.Equal(
            ["session_id", "title", "state", "last_activity", "last_cwd", "start_cwd", "prompt_count", "pid"],
            item["sessions"]!.AsArray()[0]!.AsObject().Select(pair => pair.Key));
    }

    [Fact]
    public void A_filtered_view_renders_only_the_kept_items()
    {
        var report = WorkFixtures.Report();
        var kept = WorkJoin.FilterItems(report.Items, new ItemFilter { IncludeQuietTrunk = true, Repo = "other" });

        var payload = WorkJson.Report(report, kept);

        Assert.Equal(3, payload["items"]!.AsArray().Count);
        Assert.All(payload["items"]!.AsArray(), item => Assert.Equal("other/lib", item!["repo"]!.GetValue<string>()));
    }

    [Fact]
    public void IsoTime_matches_recorded_datetime_isoformat()
    {
        Assert.Equal("2026-10-10T12:00:00+00:00", IsoTime.Format(WorkFixtures.Now));
        Assert.Equal(
            "2026-10-10T12:00:00.123456+01:00",
            IsoTime.Format(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(1)).AddTicks(1_234_560)));
        Assert.Null(IsoTime.Format(null));
        Assert.Equal(WorkFixtures.Now, IsoTime.Parse("2026-10-10T12:00:00+00:00"));
        Assert.Null(IsoTime.Parse("soon"));
        Assert.Null(IsoTime.Parse(null));
    }

    [Theory]
    [InlineData(WorkState.Active, "active")]
    [InlineData(WorkState.Merged, "merged")]
    [InlineData(WorkState.Closed, "closed")]
    [InlineData(WorkState.Stale, "stale")]
    [InlineData(WorkState.Trunk, "trunk")]
    [InlineData(WorkState.Unattached, "unattached")]
    public void WorkState_wire_names_match_the_schema(WorkState state, string wire) => Assert.Equal(wire, state.Wire());

    [Theory]
    [InlineData(PrStatus.Open, "open")]
    [InlineData(PrStatus.Draft, "draft")]
    [InlineData(PrStatus.Merged, "merged")]
    [InlineData(PrStatus.Closed, "closed")]
    public void PrStatus_wire_names_match_the_schema(PrStatus status, string wire) => Assert.Equal(wire, status.Wire());

    [Theory]
    [InlineData(PrLookup.Ok, "ok")]
    [InlineData(PrLookup.Skipped, "skipped")]
    [InlineData(PrLookup.Unavailable, "unavailable")]
    public void PrLookup_wire_names_match_the_schema(PrLookup lookup, string wire) => Assert.Equal(wire, lookup.Wire());

    [Theory]
    [InlineData(WorkSessionState.Live, "live")]
    [InlineData(WorkSessionState.WaitingOnUser, "waiting-on-user")]
    [InlineData(WorkSessionState.DiedMidTask, "died-mid-task")]
    [InlineData(WorkSessionState.DiedIdle, "died-idle")]
    [InlineData(WorkSessionState.EndedClean, "ended-clean")]
    [InlineData(WorkSessionState.Cleared, "cleared")]
    public void SessionState_wire_names_match_the_schema(WorkSessionState state, string wire) => Assert.Equal(wire, state.Wire());

    private static string FixturePath() => Path.Join(AppContext.BaseDirectory, "Work", "work-list.fixture.json");

    // Normalise line endings first, then drop the one trailing newline: a CRLF fixture must not leave a '\r'.
    private static string NormaliseFixture(string text) => text.ReplaceLineEndings("\n").TrimEnd('\n');
}
