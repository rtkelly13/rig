using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Tests.Policy;

/// <summary>The Python behaviours the policy port reproduces: <c>fnmatch</c>, <c>shlex</c> and <c>json.dumps</c>.</summary>
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
    public void Dumps_matches_python_indent_two()
    {
        var node = JsonNode.Parse("""{"a": [1, -0, 2.5, true, false, null, {}, []], "é\"\\\n\r\t\b\f\u0001": {"k": "日本"}}""");

        Assert.Equal(
            "{\n  \"a\": [\n    1,\n    0,\n    2.5,\n    true,\n    false,\n    null,\n    {},\n    []\n  ],\n"
            + "  \"\\u00e9\\\"\\\\\\n\\r\\t\\b\\f\\u0001\": {\n    \"k\": \"\\u65e5\\u672c\"\n  }\n}",
            PyJson.Dumps(node));
        Assert.Equal("null", PyJson.Dumps(null));
        Assert.Equal("1e+16", PyJson.Dumps(JsonNode.Parse("1e16")));
    }

    [Fact]
    public void Dumps_without_ensure_ascii_keeps_text_and_still_escapes_controls() =>
        Assert.Equal("\"é日本\\u0001\\n\u007f\"", PyJson.Dumps(JsonValue.Create("é日本\u0001\n\u007f"), ensureAscii: false));

    [Fact]
    public void Dumps_line_is_compact_with_python_separators() =>
        Assert.Equal(
            "{\"a\": 1, \"b\": [1, {}, []], \"c\": null, \"d\": \"\\u00e9\"}",
            PyJson.DumpsLine(JsonNode.Parse("""{"a": 1, "b": [1, {}, []], "c": null, "d": "é"}""")));

    [Fact]
    public void Dumps_with_ensure_ascii_escapes_delete() =>
        Assert.Equal("\"\\u007f\"", PyJson.Dumps(JsonValue.Create("\u007f")));

    [Fact]
    public void Whole_floats_built_in_csharp_print_as_floats_and_parsed_integers_stay_integers()
    {
        var node = new JsonArray(1.0, 30.0f, 2m, 3, 4L, JsonNode.Parse("5"), JsonNode.Parse("6.0"));

        Assert.Equal("[\n  1.0,\n  30.0,\n  2.0,\n  3,\n  4,\n  5,\n  6.0\n]", PyJson.Dumps(node));
    }

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(2.5, "2.5")]
    [InlineData(-123.456, "-123.456")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.25e-7, "1.25e-07")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1234567890123456.0, "1234567890123456.0")]
    [InlineData(1.5e300, "1.5e+300")]
    [InlineData(100.0, "100.0")]
    public void Floats_print_as_python_repr(double value, string text) => Assert.Equal(text, PyJson.FloatRepr(value));
}
