using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Tests.Text;

/// <summary>
/// The shared serializer settings: every one writes LF on every platform (#260), and the four spellings the recorded
/// <c>json.dumps</c> output used stay distinct.
/// </summary>
public sealed class JsonOutputOptionsTests
{
    /// <summary>Each shared option by name, so a theory can name it in the run.</summary>
    public static TheoryData<string> Shared => new(
        nameof(JsonOutputOptions.Indented),
        nameof(JsonOutputOptions.IndentedUnicode),
        nameof(JsonOutputOptions.Compact),
        nameof(JsonOutputOptions.CompactUnicode),
        nameof(JsonOutputOptions.SnakeCaseIndented),
        nameof(JsonOutputOptions.SnakeCaseCompact));

    [Theory]
    [MemberData(nameof(Shared))]
    public void Every_shared_option_writes_LF(string name)
    {
        var options = Named(name);

        var text = JsonSerializer.Serialize(new Payload("é"), options);

        Assert.Equal("\n", options.NewLine);
        Assert.DoesNotContain('\r', text);
    }

    [Theory]
    [MemberData(nameof(Shared))]
    public void Every_shared_option_round_trips(string name)
    {
        var options = Named(name);

        var text = JsonSerializer.Serialize(new Payload("é"), options);

        Assert.Contains(JsonNode.Parse(text)!.AsObject(), pair => pair.Value!.GetValue<string>() == "é");
    }

    [Fact]
    public void Indented_is_two_space_indented_json()
    {
        var text = JsonSerializer.Serialize(new JsonObject { ["a"] = new JsonArray(1, 2) }, JsonOutputOptions.Indented);

        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ]\n}", text);
    }

    [Theory]
    [InlineData(nameof(JsonOutputOptions.Compact))]
    [InlineData(nameof(JsonOutputOptions.CompactUnicode))]
    [InlineData(nameof(JsonOutputOptions.SnakeCaseCompact))]
    public void Compact_is_one_line(string name)
    {
        var text = JsonSerializer.Serialize(new JsonObject { ["a"] = new JsonArray(1, 2) }, Named(name));

        Assert.DoesNotContain('\n', text);
    }

    [Theory]
    [InlineData(nameof(JsonOutputOptions.Indented), "\\u00E9")]
    [InlineData(nameof(JsonOutputOptions.Compact), "\\u00E9")]
    [InlineData(nameof(JsonOutputOptions.IndentedUnicode), "é")]
    [InlineData(nameof(JsonOutputOptions.CompactUnicode), "é")]
    [InlineData(nameof(JsonOutputOptions.SnakeCaseIndented), "é")]
    [InlineData(nameof(JsonOutputOptions.SnakeCaseCompact), "é")]
    public void Unicode_options_keep_non_ascii_and_the_others_escape_it(string name, string expected)
    {
        var text = JsonSerializer.Serialize(new JsonObject { ["k"] = "é" }, Named(name));

        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(JsonOutputOptions.SnakeCaseIndented))]
    [InlineData(nameof(JsonOutputOptions.SnakeCaseCompact))]
    public void Snake_case_options_lower_case_the_property_names(string name)
    {
        var text = JsonSerializer.Serialize(new Payload("x"), Named(name));

        Assert.Equal("x", JsonNode.Parse(text)!["given_name"]!.GetValue<string>());
    }

    [Fact]
    public void The_other_options_keep_the_property_names() =>
        Assert.Equal("x", JsonNode.Parse(JsonSerializer.Serialize(new Payload("x"), JsonOutputOptions.Indented))!["GivenName"]!.GetValue<string>());

    private static JsonSerializerOptions Named(string name) => name switch
    {
        nameof(JsonOutputOptions.Indented) => JsonOutputOptions.Indented,
        nameof(JsonOutputOptions.IndentedUnicode) => JsonOutputOptions.IndentedUnicode,
        nameof(JsonOutputOptions.Compact) => JsonOutputOptions.Compact,
        nameof(JsonOutputOptions.CompactUnicode) => JsonOutputOptions.CompactUnicode,
        nameof(JsonOutputOptions.SnakeCaseIndented) => JsonOutputOptions.SnakeCaseIndented,
        nameof(JsonOutputOptions.SnakeCaseCompact) => JsonOutputOptions.SnakeCaseCompact,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a shared JSON option"),
    };

    private sealed record Payload(string GivenName);
}
