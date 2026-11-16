using System.Text.Json.Nodes;
using Rig.Core.Policy;
using Rig.Core.Text;

namespace Rig.Core.Tests.Policy;

/// <summary>The Python behaviours the policy port reproduces (<c>fnmatch</c>, <c>shlex</c>) and the JSON rig writes.</summary>
public sealed class TextFormatTests
{
    [Theory]
    [InlineData("git status", "git status", true)]
    [InlineData("git status -s", "git status*", true)]
    [InlineData("git stat", "git stat?s", false)]
    [InlineData("git stats", "git stat?", true)]
    [InlineData("a1", "a[0-9]", true)]
    [InlineData("ab", "a[!0-9]", true)]
    [InlineData("a1", "a[!0-9]", false)]
    [InlineData("a]", "a[]]", true)]
    [InlineData("a[", "a[", true)]
    [InlineData("a.b", "a.b", true)]
    [InlineData("axb", "a.b", false)]
    [InlineData("line\nbreak", "line*", true)]
    [InlineData("a\\b", "a[\\]b", true)]
    public void Globs_match_as_fnmatch_does(string name, string pattern, bool matches) =>
        Assert.Equal(matches, Glob.Matches(name, pattern, windows: false));

    [Fact]
    public void On_Windows_globs_ignore_case_and_slash_direction()
    {
        Assert.True(Glob.Matches("SRC/App", "src\\app", windows: true));
        Assert.False(Glob.Matches("SRC/App", "src\\app", windows: false));
    }

    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("echo 'a b' \"c d\"", new[] { "echo", "a b", "c d" })]
    [InlineData("a\\ b", new[] { "a b" })]
    [InlineData("\"a\\\"b\\$c\\x\"", new[] { "a\"b\\$c\\x" })]
    [InlineData("x''y", new[] { "xy" })]
    [InlineData("''", new[] { "" })]
    [InlineData("a#b", new[] { "a#b" })]
    [InlineData("a;b", new[] { "a;b" })]
    [InlineData("", new string[0])]
    public void Split_matches_shlex_split(string text, string[] tokens) => Assert.Equal(tokens, Shlex.Split(text));

    [Theory]
    [InlineData("a;b", new[] { "a", ";", "b" })]
    [InlineData("a && b || c", new[] { "a", "&&", "b", "||", "c" })]
    [InlineData("a>>b", new[] { "a", ">>", "b" })]
    [InlineData("a # comment ; x\nb", new[] { "a", "b" })]
    [InlineData("'a;b' c\\;d", new[] { "a;b", "c;d" })]
    [InlineData("x#y", new[] { "x" })]
    public void Shell_tokens_split_out_punctuation_and_drop_comments(string text, string[] tokens) =>
        Assert.Equal(tokens, Shlex.ShellTokens(text));

    [Theory]
    [InlineData("'open")]
    [InlineData("\"open")]
    [InlineData("trailing\\")]
    [InlineData("\"escaped\\")]
    public void Unbalanced_quotes_and_escapes_are_errors(string text) =>
        _ = Assert.Throws<FormatException>(() => Shlex.Split(text));

    [Fact]
    public void Format_is_two_space_indented_json()
    {
        var node = JsonNode.Parse("""{"a": [1, 2.5, true, false, null, {}, []], "k": "v"}""");

        Assert.Equal(
            "{\n  \"a\": [\n    1,\n    2.5,\n    true,\n    false,\n    null,\n    {},\n    []\n  ],\n  \"k\": \"v\"\n}",
            JsonOutput.Format(node));
        Assert.Equal("null", JsonOutput.Format(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format_writes_LF_on_every_platform(bool keepNonAscii)
    {
        var node = JsonNode.Parse("""{"a": [1, {"b": 2}]}""");

        var text = JsonOutput.Format(node, keepNonAscii);

        Assert.DoesNotContain('\r', text);
        Assert.Equal("{\n  \"a\": [\n    1,\n    {\n      \"b\": 2\n    }\n  ]\n}", text);
    }

    [Fact]
    public void Format_escapes_non_ascii_by_default_and_keeps_it_on_request()
    {
        var node = JsonNode.Parse("""{"k": "é日本"}""");

        Assert.Contains("\\u00E9", JsonOutput.Format(node), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("é", JsonOutput.Format(node), StringComparison.Ordinal);
        Assert.Contains("é日本", JsonOutput.Format(node, keepNonAscii: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Line_is_one_line_that_parses_back_to_the_same_node()
    {
        var node = JsonNode.Parse("""{"a": 1, "b": [1, {}, []], "c": null, "d": "é"}""");
        var text = JsonOutput.Line(node);

        Assert.DoesNotContain('\n', text);
        Assert.Equal(node!.ToJsonString(), JsonNode.Parse(text)!.ToJsonString());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(1e16)]
    [InlineData(1.25e-7)]
    public void Number_is_a_json_number_that_parses_back(double value) =>
        Assert.Equal(value, JsonNode.Parse(JsonOutput.Number(value))!.GetValue<double>());

    [Theory]
    [InlineData("18446744073709551617")]
    [InlineData("-9223372036854775809")]
    public void Parsed_integers_past_64_bits_keep_every_digit(string number) =>
        Assert.Equal(number, JsonOutput.Format(JsonNode.Parse(number)));
}
