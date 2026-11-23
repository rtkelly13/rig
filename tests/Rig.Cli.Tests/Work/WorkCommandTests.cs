using System.Text.Json.Nodes;
using Rig.Cli.Work;
using Rig.Core.GitHub;
using Rig.Core.Tests;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.WorkItems;
using Rig.Core.Text;
using Rig.Core.WorkItems;

namespace Rig.Cli.Tests.Work;

/// <summary><c>rig work list</c> from the C# front door, over a report the test chooses.</summary>
public sealed class WorkCommandTests : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Join(Path.GetTempPath(), "code"));
    private static readonly string Home = Path.GetFullPath(Path.Join(Path.GetTempPath(), "home"));

    private readonly TempDir _temp = new();
    private readonly FakeProc _proc = new();
    private readonly StringWriter _output = new() { NewLine = "\n" };
    private readonly StringWriter _error = new() { NewLine = "\n" };
    private readonly List<WorkCollectOptions> _asked = [];
    private WorkReport _report = WorkFixtures.Report();

    public void Dispose()
    {
        _output.Dispose();
        _error.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task The_list_is_a_row_per_item_then_a_summary_with_warnings_on_stderr()
    {
        var shown = WorkJoin.FilterItems(_report.Items, new ItemFilter());

        Assert.Equal(0, await Run("work", "list"));

        var lines = Lines();
        Assert.Equal(WorkText.Columns, lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(shown.Count + 2, lines.Length);
        Assert.StartsWith(shown[0].State.Wire(), lines[1], StringComparison.Ordinal);
        Assert.Equal(WorkText.Summary(_report.Items.Count, shown), lines[^1]);
        Assert.Equal("warning: acme/app: gh lookup failed (offline, unauthenticated or no access)\n", _error.ToString());
        Assert.Empty(_proc.Requests);
    }

    [Fact]
    public async Task Sessions_are_listed_under_their_item_when_asked()
    {
        var shown = WorkJoin.FilterItems(_report.Items, new ItemFilter());

        Assert.Equal(0, await Run("work", "list", "--sessions"));

        Assert.Equal(shown.Count + shown.Sum(item => item.Sessions.Count) + 2, Lines().Length);
        Assert.Contains("  0b1c2d3e", _output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Fix hub partitions", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_is_the_only_thing_on_stdout_and_nothing_reaches_stderr()
    {
        var shown = WorkJoin.FilterItems(_report.Items, new ItemFilter());

        Assert.Equal(0, await Run("work", "list", "--json"));

        Assert.Equal(WorkJson.Render(_report, shown) + "\n", _output.ToString());
        Assert.Equal(shown.Count, JsonNode.Parse(_output.ToString())!["items"]!.AsArray().Count);
        Assert.Empty(_error.ToString());
    }

    [Fact]
    public async Task Filters_narrow_the_list_and_the_summary_counts_what_they_hid()
    {
        var since = WorkFixtures.Now - TimeSpan.FromHours(2);
        (string[] Args, ItemFilter Filter)[] views =
        [
            (["--all"], new ItemFilter { IncludeQuietTrunk = true }),
            (["--repo", "OTHER"], new ItemFilter { Repo = "OTHER" }),
            (["--state", "Trunk", "--state", "merged"], new ItemFilter { States = new HashSet<WorkState> { WorkState.Trunk, WorkState.Merged } }),
            (["--since", "2h"], new ItemFilter { Since = since }),
            (["--since", since.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)], new ItemFilter { Since = since }),
            (["--repo", "no-such-repo"], new ItemFilter { Repo = "no-such-repo" }),
        ];

        foreach (var (args, filter) in views)
        {
            var shown = WorkJoin.FilterItems(_report.Items, filter);

            Assert.Equal(0, await Run(["work", "list", .. args]));

            Assert.Equal(WorkText.Summary(_report.Items.Count, shown), Lines()[^1]);
            Assert.Equal(shown.Count + 2, Lines().Length);
        }
    }

    [Theory]
    [InlineData("--state", "busy", "rig work list: --state: 'busy' is not one of active, merged, closed, stale, trunk, unattached.\n")]
    [InlineData("--since", "soon", "rig work list: --since: expected 7d / 24h / 2w or an ISO date, got 'soon'\n")]
    public async Task A_filter_that_cannot_be_read_is_refused_before_anything_is_collected(string option, string value, string message)
    {
        Assert.Equal(2, await Run("work", "list", option, value));

        Assert.Equal(message, _error.ToString());
        Assert.Empty(_output.ToString());
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task Timings_go_to_stderr_and_leave_stdout_to_the_json()
    {
        Assert.Equal(0, await Run("work", "list", "--timings", "--json"));

        Assert.Matches(@"\Atimings: git refs=\d+\.\d\ds  sessions=\d+\.\d\ds  wall=1\.50s\n\z", _error.ToString());
        Assert.StartsWith("{", _output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_collection_is_asked_for_what_the_options_say()
    {
        Assert.Equal(0, await Run("work", "list"));
        Assert.Equal(0, await Run("work", "list", "--root", "~/src", "--offline", "--refresh", "--no-status", "--stale-days", "3", "--session-days", "9"));
        Assert.Equal(0, await Run("work", "list", "--root", "~"));
        Assert.Equal(0, await Run("work", "list", "--root", _temp.Path));

        Assert.Equal(new WorkCollectOptions(Root), _asked[0]);
        Assert.Equal(
            new WorkCollectOptions(Path.Join(Home, "src")) { SessionWindow = TimeSpan.FromDays(9), StaleAfter = TimeSpan.FromDays(3), Offline = true, Refresh = true, Probe = false },
            _asked[1]);
        Assert.Equal([Home, Path.GetFullPath(_temp.Path)], _asked.Skip(2).Select(options => options.Root));
    }

    [Fact]
    public async Task A_cell_stays_on_one_line_whatever_a_title_or_warning_holds()
    {
        var item = _report.Items.First(candidate => candidate.Sessions.Count > 0);
        var hostile = item with { Sessions = [item.Sessions[0] with { Title = "first\nsecond \e[31mred\e[0m\ttab" }] };
        _report = _report with { Items = [hostile], Warnings = ["gone\r\nwrong"] };

        Assert.Equal(0, await Run("work", "list", "--sessions", "--all"));

        Assert.Equal(4, Lines().Length);
        Assert.EndsWith("first second red tab", Lines()[2], StringComparison.Ordinal);
        Assert.Equal("warning: gone  wrong\n", _error.ToString());
    }

    [Fact]
    public async Task The_group_without_its_verb_is_an_error()
    {
        Assert.NotEqual(0, await Run("work"));

        Assert.Empty(_asked);
    }

    private string[] Lines() => _output.ToString().TrimEnd('\n').Split('\n');

    private async Task<int> Run(params string[] args)
    {
        _output.GetStringBuilder().Clear();
        _error.GetStringBuilder().Clear();
        var command = new WorkCommand(new WorkHost(Root, Home), Collect, new FixedClock(WorkFixtures.Now), _output, _error);
        return await new RigApp(new FakeGitHub(), _ => Task.FromResult<RepoSlug?>(null),
            _output, _error, [command.Build]).RunAsync(args, TestContext.Current.CancellationToken);
    }

    private Task<Collected> Collect(WorkCollectOptions options, CancellationToken cancellationToken)
    {
        _asked.Add(options);
        var timings = new WorkTimings();
        _ = timings.Measure("git refs", () => 0);
        _ = timings.Measure("sessions", () => 0);
        return Task.FromResult(new Collected(_report, timings, 1.5));
    }
}
