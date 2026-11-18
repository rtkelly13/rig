using System.Text.Json.Nodes;
using Rig.Core.Desktop;
using Rig.Core.Text;

namespace Rig.Core.Tests.Desktop;

/// <summary>
/// Plan values are read strictly: a number is a JSON integer, a flag a JSON boolean, text a JSON string, and a rejection
/// names the source, the JSON path, what was expected and what was found.
/// </summary>
public sealed class PlanDecodingTests
{
    [Theory]
    [InlineData("900")]
    [InlineData("-1")]
    public void A_whole_number_is_a_delay(string json) =>
        Assert.Equal(int.Parse(json, System.Globalization.CultureInfo.InvariantCulture), Entry($$"""{"name": "n", "delay_ms": {{json}}}""").DelayMs);

    [Theory]
    [InlineData("1.0", "the number 1.0")]
    [InlineData("1.5", "the number 1.5")]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("\"900\"", "the string \"900\"")]
    [InlineData("\"1.0\"", "the string \"1.0\"")]
    [InlineData("99999999999", "the number 99999999999")]
    public void A_delay_that_is_not_a_whole_number_says_what_it_found(string json, string found) =>
        Assert.Equal(
            $"<memory>: $.desktops[0].launch[0].delay_ms: expected a whole number, found {found}",
            Assert.Throws<JsonInputException>(() => Entry($$"""{"name": "n", "delay_ms": {{json}}}""")).Message);

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void A_flag_is_a_json_boolean(string json, bool expected) =>
        Assert.Equal(expected, Entry($$"""{"name": "n", "single_instance": {{json}}}""").SingleInstance);

    [Theory]
    [InlineData("1", "the number 1")]
    [InlineData("0", "the number 0")]
    [InlineData("\"yes\"", "the string \"yes\"")]
    [InlineData("\"false\"", "the string \"false\"")]
    [InlineData("null", "null")]
    [InlineData("[]", "a list")]
    public void A_flag_that_is_not_a_boolean_says_what_it_found(string json, string found) =>
        Assert.Equal(
            $"<memory>: $.desktops[0].launch[0].single_instance: expected true or false, found {found}",
            Assert.Throws<JsonInputException>(() => Entry($$"""{"name": "n", "single_instance": {{json}}}""")).Message);

    [Fact]
    public void An_optional_timeout_is_a_whole_number_or_absent()
    {
        Assert.Equal(5, Entry("""{"name": "n", "window_wait_timeout_ms": 5}""").WindowWaitTimeoutMs);
        Assert.Null(Entry("""{"name": "n"}""").WindowWaitTimeoutMs);
    }

    [Theory]
    [InlineData("""{"name": "n", "window_wait_timeout_ms": "5"}""", "$.desktops[0].launch[0].window_wait_timeout_ms: expected a whole number, found the string \"5\"")]
    [InlineData("""{"name": "n", "window_wait_timeout_ms": null}""", "$.desktops[0].launch[0].window_wait_timeout_ms: expected a whole number, found null")]
    [InlineData("""{"name": "n", "path": 1}""", "$.desktops[0].launch[0].path: expected a string, found the number 1")]
    [InlineData("""{"name": "n", "path": true}""", "$.desktops[0].launch[0].path: expected a string, found true")]
    [InlineData("""{"name": 5}""", "$.desktops[0].launch[0].name: expected a string, found the number 5")]
    [InlineData("""{"name": null}""", "$.desktops[0].launch[0].name: expected a string, found null")]
    [InlineData("""{}""", "$.desktops[0].launch[0].name: expected a string, found nothing")]
    [InlineData("""{"name": "n", "args": "a"}""", "$.desktops[0].launch[0].args: expected a list, found the string \"a\"")]
    [InlineData("""{"name": "n", "args": ["a", 1]}""", "$.desktops[0].launch[0].args[1]: expected a string, found the number 1")]
    [InlineData("""{"name": "n", "args": ["a", null]}""", "$.desktops[0].launch[0].args[1]: expected a string, found null")]
    [InlineData("""{"name": "n", "placement": "x"}""", "$.desktops[0].launch[0].placement: expected an object, found the string \"x\"")]
    [InlineData("""{"name": "n", "placement": {"layout": {"mode": "full"}}}""", "$.desktops[0].launch[0].placement.screen: expected a whole number, found nothing")]
    [InlineData("""{"name": "n", "placement": {"screen": "2", "layout": {"mode": "full"}}}""", "$.desktops[0].launch[0].placement.screen: expected a whole number, found the string \"2\"")]
    [InlineData("""{"name": "n", "placement": {"screen": 1}}""", "$.desktops[0].launch[0].placement.layout: expected an object, found nothing")]
    [InlineData("""{"name": "n", "placement": {"screen": 1, "layout": {"mode": "thirds", "slot": "2"}}}""", "$.desktops[0].launch[0].placement.layout.slot: expected a whole number, found the string \"2\"")]
    [InlineData("""{"name": "n", "placement": {"screen": 1, "layout": {"mode": "columns", "min_width": "800"}}}""", "$.desktops[0].launch[0].placement.layout.min_width: expected a whole number, found the string \"800\"")]
    [InlineData("""{"name": "n", "placement": {"screen": 1, "layout": {"mode": "full"}, "subdivide": []}}""", "$.desktops[0].launch[0].placement.subdivide: expected an object, found a list")]
    [InlineData("""{"name": "n", "colour": "red"}""", "$.desktops[0].launch[0].colour: expected a known field (")]
    public void A_rejected_value_names_its_path_what_was_expected_and_what_was_found(string json, string message)
    {
        var error = Assert.Throws<JsonInputException>(() => Entry(json));

        Assert.StartsWith("<memory>: " + message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejection_names_the_file_it_came_from() =>
        Assert.Equal(
            "plan.json: $.switch_delay_ms: expected a whole number, found the string \"650\"",
            Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"switch_delay_ms": "650"}""", "plan.json")).Message);

    [Fact]
    public void An_unknown_field_is_rejected_at_every_level()
    {
        Assert.Contains("$.personal_workspace: expected a known field", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"personal_workspace": "q"}""")).Message, StringComparison.Ordinal);
        Assert.Contains("$.desktops[0].extra: expected a known field", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"desktops": [{"name": "d", "extra": 1}]}""")).Message, StringComparison.Ordinal);
        Assert.Contains("$.desktops[0].captured_windows[0].extra", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"desktops": [{"name": "d", "captured_windows": [{"title": "t", "extra": 1}]}]}""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_fields_hold_json_strings_only()
    {
        Assert.Equal("5", Entry("""{"name": "5"}""").Name);
        Assert.Equal(["1"], PlanFiles.Parse("""{"notes": ["1"]}""").Notes);
        Assert.Equal("$.notes[0]: expected a string, found the number 1", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"notes": [1]}""")).Message["<memory>: ".Length..]);
        Assert.Equal("$.aliases.n: expected a string, found the number 2", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"aliases": {"n": 2}}""")).Message["<memory>: ".Length..]);
    }

    [Fact]
    public void A_placement_reads_whole_numbers()
    {
        var placement = Entry("""{"name": "n", "placement": {"screen": 2, "layout": {"mode": "thirds", "slot": 2}}}""").Placement;

        Assert.Equal(new PlacementSpec(2, new PlacementLayout("thirds", 2)), placement);
    }

    [Fact]
    public void Feature_defaults_are_json_booleans()
    {
        var plan = PlanFiles.Parse("""{"features": {"a": true, "b": false}}""");

        Assert.Equal([new("a", true), new("b", false)], plan.Features.ToList());
    }

    [Theory]
    [InlineData("""{"features": {"a": "on"}}""", "$.features.a: expected true or false, found the string \"on\"")]
    [InlineData("""{"features": {"a": 1}}""", "$.features.a: expected true or false, found the number 1")]
    [InlineData("""{"features": {"a": null}}""", "$.features.a: expected true or false, found null")]
    [InlineData("""{"features": [1]}""", "$.features: expected an object, found a list")]
    public void A_feature_default_that_is_not_a_boolean_is_rejected(string json, string message) =>
        Assert.Equal(message, Assert.Throws<JsonInputException>(() => PlanFiles.Parse(json)).Message["<memory>: ".Length..]);

    [Fact]
    public void Window_bounds_are_an_object_of_four_whole_numbers()
    {
        static Rect? Bounds(string json) => PlanFiles.Parse($$"""{"desktops": [{"name": "d", "captured_windows": [{"title": "t", "bounds": {{json}}}]}]}""").Desktops[0].CapturedWindows[0].Bounds;
        static string Rejected(string json) => Assert.Throws<JsonInputException>(() => Bounds(json)).Message["<memory>: ".Length..];

        Assert.Equal(new Rect(1, 2, 3, 4), Bounds("""{"left": 1, "top": 2, "width": 3, "height": 4}"""));
        Assert.Equal("$.desktops[0].captured_windows[0].bounds.top: expected a whole number, found nothing", Rejected("""{"left": 1}"""));
        Assert.Equal("$.desktops[0].captured_windows[0].bounds: expected an object, found a list", Rejected("[1, 2, 3, 4]"));
        Assert.Equal("$.desktops[0].captured_windows[0].bounds: expected an object, found the string \"x\"", Rejected("\"x\""));
    }

    [Fact]
    public void A_window_pid_or_placement_of_the_wrong_type_is_rejected()
    {
        var error = Assert.Throws<JsonInputException>(() => PlanFiles.Parse("""{"desktops": [{"name": "d", "captured_windows": [{"title": "t", "pid": "unknown"}]}]}"""));

        Assert.Equal("<memory>: $.desktops[0].captured_windows[0].pid: expected a whole number, found the string \"unknown\"", error.Message);
    }

    [Fact]
    public void The_hyphenated_personal_workspace_key_is_the_one_read()
    {
        Assert.Equal("p", PlanFiles.Parse("""{"personal-workspace": "p"}""").PersonalWorkspace);
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
    [InlineData("1")]
    [InlineData("2")]
    public void The_version_is_a_whole_number(string json) =>
        Assert.Equal(int.Parse(json, System.Globalization.CultureInfo.InvariantCulture), PlanFiles.Parse($$"""{"version": {{json}}}""").Version);

    [Theory]
    [InlineData("1.0", "the number 1.0")]
    [InlineData("\"2\"", "the string \"2\"")]
    [InlineData("true", "true")]
    public void A_version_that_is_not_a_whole_number_says_what_it_found(string json, string found) =>
        Assert.Equal($"<memory>: $.version: expected a whole number, found {found}", Assert.Throws<JsonInputException>(() => PlanFiles.Parse($$"""{"version": {{json}}}""")).Message);

    [Fact]
    public void A_plan_that_is_not_json_or_not_an_object_says_so()
    {
        Assert.Contains("plan.json: $: not valid JSON", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("{", "plan.json")).Message, StringComparison.Ordinal);
        Assert.Equal("<memory>: $: expected an object, found a list", Assert.Throws<JsonInputException>(() => PlanFiles.Parse("[]")).Message);
    }

    [Fact]
    public void Rect_edges_and_centres_floor()
    {
        var rect = new Rect(-10, 5, 7, 9);

        Assert.Equal((-3, 14, -7, 9), (rect.Right, rect.Bottom, rect.CenterX, rect.CenterY));
    }

    private static LaunchEntry Entry(string json) =>
        PlanFiles.Parse($$"""{"desktops": [{"name": "d", "launch": [{{json}}]}]}""").Desktops[0].Launch[0];

    // The recorded writer's separators, no indent: ", " and ": " separators.
    private static string Compact(JsonNode node) => node switch
    {
        JsonObject map => "{" + string.Join(", ", map.Select(item => $"\"{item.Key}\": {Compact(item.Value!)}")) + "}",
        JsonArray list => "[" + string.Join(", ", list.Select(item => Compact(item!))) + "]",
        _ => node.ToJsonString(),
    };
}
