using System.Text.Json.Nodes;
using Rig.Core.Desktop;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Hand-edited plan values read the way the Python reader's lax (pydantic) decoding reads them. Each expectation was
/// taken from running the Python model on the same input.
/// </summary>
public sealed class PlanDecodingTests
{
    [Theory]
    [InlineData("900", 900)]
    [InlineData("1.0", 1)]
    [InlineData("\"900\"", 900)]
    [InlineData("\" 900 \"", 900)]
    [InlineData("true", 1)]
    [InlineData("\"1.0\"", 1)]
    public void A_whole_number_in_any_spelling_is_a_delay(string json, int expected) =>
        Assert.Equal(expected, Entry($$"""{"name": "n", "delay_ms": {{json}}}""").DelayMs);

    [Theory]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("\"1e3\"")]
    public void A_delay_that_is_not_a_whole_number_is_an_error(string json) =>
        Assert.Throws<InvalidPlanException>(() => Entry($$"""{"name": "n", "delay_ms": {{json}}}"""));

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("1.0", true)]
    [InlineData("\"yes\"", true)]
    [InlineData("\"on\"", true)]
    [InlineData("\"off\"", false)]
    [InlineData("\"t\"", true)]
    [InlineData("\"Y\"", true)]
    [InlineData("\"TRUE\"", true)]
    public void A_yes_or_no_in_any_spelling_is_a_flag(string json, bool expected) =>
        Assert.Equal(expected, Entry($$"""{"name": "n", "single_instance": {{json}}}""").SingleInstance);

    [Theory]
    [InlineData("2")]
    [InlineData("\"nope\"")]
    [InlineData("null")]
    public void A_flag_that_is_neither_yes_nor_no_is_an_error(string json) =>
        Assert.Throws<InvalidPlanException>(() => Entry($$"""{"name": "n", "single_instance": {{json}}}"""));

    [Theory]
    [InlineData("5", 5)]
    [InlineData("\"5\"", 5)]
    [InlineData("true", 1)]
    [InlineData("1.5", null)]
    [InlineData("\"x\"", null)]
    [InlineData("null", null)]
    public void An_unreadable_optional_timeout_is_absent(string json, int? expected) =>
        Assert.Equal(expected, Entry($$"""{"name": "n", "window_wait_timeout_ms": {{json}}}""").WindowWaitTimeoutMs);

    [Theory]
    [InlineData("1", "1")]
    [InlineData("1.5", "1.5")]
    [InlineData("true", "True")]
    [InlineData("1e20", "1e+20")]
    [InlineData("100000000000000000000", "100000000000000000000")]
    public void A_number_or_boolean_where_text_belongs_reads_as_python_prints_it(string json, string expected) =>
        Assert.Equal(expected, Entry($$"""{"name": "n", "path": {{json}}}""").Path);

    [Theory]
    [InlineData("""{"name": "n", "path": false}""", "False")]
    [InlineData("""{"name": "n", "delay_ms": false}""", "0")]
    public void False_reads_as_python_prints_it(string json, string expected)
    {
        var entry = Entry(json);

        Assert.Equal(expected, entry.Path ?? entry.DelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("""{"name": "n", "single_instance": []}""")]
    [InlineData("""{"name": "n", "placement": {"layout": {"mode": "full"}}}""")]
    [InlineData("""{"name": "n", "placement": {"screen": [], "layout": {"mode": "full"}}}""")]
    public void A_value_of_the_wrong_kind_where_one_is_required_is_an_error(string json) =>
        Assert.Throws<InvalidPlanException>(() => Entry(json));

    [Fact]
    public void Args_read_every_scalar_as_text_and_anything_but_a_list_as_none()
    {
        Assert.Equal(["a", "1", "2.5", "True"], Entry("""{"name": "n", "args": ["a", 1, 2.5, true]}""").Args);
        Assert.Empty(Entry("""{"name": "n", "args": "a"}""").Args);
        Assert.Throws<InvalidPlanException>(() => Entry("""{"name": "n", "args": ["a", null]}"""));
    }

    [Fact]
    public void An_entry_needs_a_name_but_any_scalar_will_do()
    {
        Assert.Equal("5", Entry("""{"name": 5}""").Name);
        Assert.Equal("True", Entry("""{"name": true}""").Name);
        Assert.Throws<InvalidPlanException>(() => Entry("""{"name": null}"""));
    }

    [Fact]
    public void A_placement_reads_lax_numbers_and_drops_what_it_cannot_read()
    {
        var placement = Entry("""{"name": "n", "placement": {"screen": "2", "layout": {"mode": "thirds", "slot": "2", "min_width": "x"}}}""").Placement;

        Assert.Equal(new PlacementSpec(2, new PlacementLayout("thirds", 2)), placement);
        Assert.Null(Entry("""{"name": "n", "placement": "x"}""").Placement);
        Assert.Throws<InvalidPlanException>(() => Entry("""{"name": "n", "placement": {"screen": 1}}"""));
    }

    [Fact]
    public void Feature_defaults_accept_words_and_truthiness_and_anything_else_is_off()
    {
        var plan = PlanFiles.Parse("""{"features": {"a": "on", "b": "off", "c": 1, "d": 0, "e": null, "f": [], "g": "maybe"}}""");

        Assert.Equal(
            [new("a", true), new("b", false), new("c", true), new("d", false), new("e", false), new("f", false), new("g", false)],
            plan.Features.ToList());
        Assert.Empty(PlanFiles.Parse("""{"features": [1]}""").Features);
    }

    [Fact]
    public void Window_bounds_read_from_an_object_or_four_numbers_and_otherwise_are_absent()
    {
        static Rect? Bounds(string json) => PlanFiles.Parse($$"""{"desktops": [{"name": "d", "captured_windows": [{"title": "t", "bounds": {{json}}}]}]}""").Desktops[0].CapturedWindows[0].Bounds;

        Assert.Equal(new Rect(1, 2, 3, 4), Bounds("[1, 2, 3, 4]"));
        Assert.Equal(new Rect(1, 2, 3, 4), Bounds("""{"left": 1, "top": 2, "width": 3, "height": 4}"""));
        Assert.Null(Bounds("[1, 2, 3]"));
        Assert.Null(Bounds("""{"left": 1}"""));
        Assert.Null(Bounds("\"x\""));
    }

    [Fact]
    public void An_unreadable_window_pid_or_placement_is_absent()
    {
        var window = PlanFiles.Parse("""{"desktops": [{"name": "d", "captured_windows": [{"title": "t", "pid": "unknown", "inferred_placement": "left"}]}]}""").Desktops[0].CapturedWindows[0];

        Assert.Equal((null, null), (window.Pid, window.InferredPlacement));
    }

    [Fact]
    public void Numbers_where_text_belongs_read_as_text_throughout_the_plan()
    {
        var plan = PlanFiles.Parse("""{"notes": [1], "aliases": {"n": 2}, "desktops": [{"name": 3, "launch": [{"name": "x", "args": [123], "cwd": 4}]}]}""");
        var entry = plan.Desktops[0].Launch[0];

        Assert.Equal(["1"], plan.Notes);
        Assert.Equal(("2", "3"), (plan.Aliases["n"], plan.Desktops[0].Name));
        Assert.Equal(["123"], entry.Args);
        Assert.Equal("4", entry.Cwd);
    }

    [Fact]
    public void The_hyphenated_personal_workspace_key_wins_and_retired_names_are_never_read()
    {
        Assert.Equal("p", PlanFiles.Parse("""{"personal-workspace": "p", "personal_workspace": "q"}""").PersonalWorkspace);
        Assert.Null(PlanFiles.Parse("""{"personal_workspace": "q"}""").PersonalWorkspace);
        Assert.Empty(PlanFiles.Parse("""{"retired_desktop_names": ["a"]}""").RetiredDesktopNames);
    }

    [Fact]
    public void A_terminal_without_a_shell_gets_the_default_one_and_writes_it_back()
    {
        var plan = PlanFiles.Parse("""{"desktops": [{"name": "d", "launch": [{"name": "t", "type": "terminal", "shell": ""}]}]}""");

        Assert.Equal(LaunchEntry.DefaultTerminalShell, plan.Desktops[0].Launch[0].Shell);
        Assert.Equal(
            """{"version": 1, "switch_delay_ms": 650, "desktops": [{"name": "d", "launch": [{"name": "t", "type": "terminal", "delay_ms": 900, "shell": "wt-pwsh"}]}]}""",
            Compact(plan.ToJson()));
    }

    [Theory]
    [InlineData("1.0", 1)]
    [InlineData("\"2\"", 2)]
    [InlineData("true", 1)]
    public void The_version_reads_lax_numbers(string json, int expected) =>
        Assert.Equal(expected, PlanFiles.Parse($$"""{"version": {{json}}}""").Version);

    [Fact]
    public void A_plan_that_is_not_json_or_not_an_object_says_so()
    {
        Assert.Contains("not valid JSON", Assert.Throws<InvalidPlanException>(() => PlanFiles.Parse("{", "plan.json")).Message, StringComparison.Ordinal);
        Assert.Contains("JSON objects", Assert.Throws<InvalidPlanException>(() => PlanFiles.Parse("[]")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rect_edges_and_centres_floor_like_python()
    {
        var rect = new Rect(-10, 5, 7, 9);

        Assert.Equal((-3, 14, -7, 9), (rect.Right, rect.Bottom, rect.CenterX, rect.CenterY));
    }

    private static LaunchEntry Entry(string json) =>
        PlanFiles.Parse($$"""{"desktops": [{"name": "d", "launch": [{{json}}]}]}""").Desktops[0].Launch[0];

    // Python's json.dumps without indent: ", " and ": " separators.
    private static string Compact(JsonNode node) => node switch
    {
        JsonObject map => "{" + string.Join(", ", map.Select(item => $"\"{item.Key}\": {Compact(item.Value!)}")) + "}",
        JsonArray list => "[" + string.Join(", ", list.Select(item => Compact(item!))) + "]",
        _ => node.ToJsonString(),
    };
}
