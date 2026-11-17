using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Tests.Text;

/// <summary>
/// Python's <c>repr()</c> of a string, so messages that quoted a value with <c>!r</c> read the same from C#. The
/// expected values are what Python prints for each input.
/// </summary>
public sealed class QuotedTextTests
{
    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\"", "'say \"hi\"'")]
    [InlineData("both ' and \"", "'both \\' and \"'")]
    [InlineData("a\\b", "'a\\\\b'")]
    [InlineData("line\nnext\r\tend", "'line\\nnext\\r\\tend'")]
    [InlineData("bell\a del\x7f", "'bell\\x07 del\\x7f'")]
    [InlineData("👍 é", "'👍 é'")]
    [InlineData("", "''")]
    public void Matches_python_repr(string text, string expected) => Assert.Equal(expected, QuotedText.Str(text));

    [Fact]
    public void Quote_selection_follows_python()
    {
        // A single quote alone switches to double quotes; both kinds stay inside single quotes.
        Assert.Equal("'plain'", QuotedText.Str("plain"));
        Assert.Equal("\"it's\"", QuotedText.Str("it's"));
        Assert.Equal("'\"quoted\"'", QuotedText.Str("\"quoted\""));
        Assert.Equal("'both \\' and \"'", QuotedText.Str("both ' and \""));
    }

    [Fact]
    public void List_is_python_repr_of_a_list_of_strings() =>
        Assert.Equal("['a', \"b'c\"]", QuotedText.List(["a", "b'c"]));

    [Theory]
    [InlineData(null, "None")]
    [InlineData(true, "True")]
    [InlineData(false, "False")]
    [InlineData(7, "7")]
    public void Value_is_python_str(object? value, string expected) => Assert.Equal(expected, QuotedText.Value(value));

    [Fact]
    public void Json_is_compact_with_unicode_kept()
    {
        var node = new JsonObject { ["name"] = "é", ["size"] = 2 };

        Assert.Equal("""{"name":"é","size":2}""", QuotedText.Json(node));
    }
}
