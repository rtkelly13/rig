using Rig.Core.Text;
using System.Text.Json.Nodes;
using System.Globalization;
using Rig.Core.WorkItems;

namespace Rig.Core.Tests.WorkItems;

/// <summary>
/// The text view's cells against what the Python table held for the same report (<c>work-list-text.fixture.json</c>,
/// written by <c>desktop_manager.work.cli</c> before it was retired, with its styling stripped).
/// </summary>
public sealed class WorkTextTests
{
    private static readonly DateTimeOffset Now = WorkFixtures.Now;

    [Fact]
    public void Every_row_says_what_the_python_table_said()
    {
        var report = WorkFixtures.Report();
        var rows = Fixture()["rows"]!.AsArray();

        Assert.Equal(rows.Select(row => (string)row!["key"]!), report.Items.Select(item => item.Key));
        foreach (var (item, row) in report.Items.Zip(rows))
        {
            Assert.Equal(Strings(row!["cells"]!), WorkText.Cells(item, report.GeneratedAt));
            Assert.Equal(
                row["sessions"]!.AsArray().Select(session => string.Join('|', Strings(session!))),
                item.Sessions.Select(session => string.Join('|', WorkText.SessionCells(session, report.GeneratedAt))));
        }
    }

    [Fact]
    public void A_row_has_a_cell_for_every_column()
    {
        var report = WorkFixtures.Report();
        var item = report.Items.First(candidate => candidate.Sessions.Count > 0);

        Assert.Equal(WorkText.Columns.Count, WorkText.Cells(item, Now).Count);
        Assert.Equal(WorkText.Columns.Count, WorkText.SessionCells(item.Sessions[0], Now).Count);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("all")]
    [InlineData("repo")]
    [InlineData("states")]
    [InlineData("since")]
    [InlineData("nothing")]
    public void Each_filtered_view_keeps_the_same_items_and_summary(string name)
    {
        var report = WorkFixtures.Report();
        var view = Fixture()["views"]!.AsArray().Single(candidate => (string)candidate!["name"]! == name)!;
        var spec = name switch
        {
            "all" => new ItemFilter { IncludeQuietTrunk = true },
            "repo" => new ItemFilter { Repo = "OTHER" },
            "states" => new ItemFilter { States = new HashSet<WorkState> { WorkState.Trunk, WorkState.Merged } },
            "since" => new ItemFilter { Since = Now - TimeSpan.FromHours(2) },
            "nothing" => new ItemFilter { Repo = "no-such-repo" },
            _ => new ItemFilter(),
        };

        var shown = WorkJoin.FilterItems(report.Items, spec);

        Assert.Equal(Strings(view["keys"]!), shown.Select(item => item.Key));
        Assert.Equal((string)view["summary"]!, WorkText.Summary(report.Items.Count, shown));
    }

    [Fact]
    public void Ages_read_as_python_wrote_them()
    {
        foreach (var entry in Fixture()["ago"]!.AsArray())
        {
            DateTimeOffset? moment = entry!["seconds"] is { } seconds ? Now - TimeSpan.FromSeconds((double)seconds) : null;

            Assert.Equal((string)entry["text"]!, WorkText.Ago(moment, Now));
        }
    }

    [Fact]
    public void Since_is_parsed_or_refused_as_python_did()
    {
        foreach (var entry in Fixture()["since"]!.AsArray())
        {
            var text = (string)entry!["text"]!;
            if (entry["error"] is { } error)
            {
                Assert.Equal((string)error!, Assert.Throws<FormatException>(() => WorkText.ParseSince(text, Now)).Message);
            }
            else
            {
                var parsed = WorkText.ParseSince(text, Now);
                Assert.Equal((string)entry["moment"]!, IsoTime.Format(parsed));
            }
        }
    }

    [Theory]
    [InlineData("2026-10-01T08:30", "2026-10-01T08:30:00+00:00")]
    [InlineData("2026-10-01 08:30:00.250", "2026-10-01T08:30:00.250000+00:00")]
    [InlineData("99999999999999999999d", null)]
    public void Since_reads_the_other_iso_forms_and_refuses_a_span_too_long_to_count(string text, string? expected)
    {
        if (expected is null)
        {
            _ = Assert.Throws<FormatException>(() => WorkText.ParseSince(text, Now));
            return;
        }

        Assert.Equal(expected, IsoTime.Format(WorkText.ParseSince(text, Now)));
    }

    [Fact]
    public void A_branch_ahead_and_behind_its_upstream_in_several_worktrees_says_so()
    {
        var item = WorkFixtures.Report().Items[0] with
        {
            BranchRef = new BranchRef("feature", Upstream: "origin/feature", Ahead: 2, Behind: null, Unpushed: 2),
            PrLookup = PrLookup.Unavailable,
            Pr = null,
        };
        item = item with { Checkouts = [.. item.Checkouts, .. item.Checkouts] };

        var cells = WorkText.Cells(item, Now);

        Assert.Equal("?", cells[4]);
        Assert.Contains("2 unpushed +2/-0", cells[5], StringComparison.Ordinal);
        Assert.EndsWith(string.Create(CultureInfo.InvariantCulture, $"{item.Checkouts.Count} wt"), cells[5], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_session_state_has_a_short_name()
    {
        Assert.Equal(
            ["live", "waiting", "died-mid", "died-idle", "ended", "cleared"],
            Enum.GetValues<WorkSessionState>().Select(WorkText.Brief));
    }

    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Join(AppContext.BaseDirectory, "Work", "work-list-text.fixture.json")))!.AsObject();

    private static IEnumerable<string> Strings(JsonNode array) => array.AsArray().Select(value => (string)value!);
}
