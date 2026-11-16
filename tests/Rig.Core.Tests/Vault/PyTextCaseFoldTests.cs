using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary><see cref="PyText.CaseFold"/> agrees with Python's <c>str.casefold</c> on the folds that differ from lower-casing.</summary>
public sealed class PyTextCaseFoldTests
{
    [Theory]
    [InlineData("Straße", "strasse")]
    [InlineData("ΣΟΦΟΣ", "σοφοσ")]
    [InlineData("ς", "σ")]
    [InlineData("ﬁle", "file")]
    [InlineData("ﬃ", "ffi")]
    [InlineData("µ", "μ")]
    [InlineData("ſ", "s")]
    [InlineData("ϐ", "β")]
    [InlineData("Plain ASCII", "plain ascii")]
    public void Text_folds_as_python_does(string text, string folded) => Assert.Equal(folded, PyText.CaseFold(text));

    [Fact]
    public void Repr_quotes_and_escapes()
    {
        Assert.Equal("'hello'", PyText.Repr("hello"));
        Assert.Equal("\"he'll'o\"", PyText.Repr("he'll'o"));
        Assert.Equal("'\\\\\\n\\t\\r\\x00\\x7f'", PyText.Repr("\\\n\t\r\0\x7f"));
        Assert.Equal("'a\"b'", PyText.Repr("a\"b"));
        Assert.Equal("'a\\'b\"c'", PyText.Repr("a'b\"c"));
        Assert.Equal("'a\\x1b'", PyText.Repr("a\x01b"));
    }

    [Fact]
    public void Strip_removes_whitespace_or_custom_chars()
    {
        Assert.Equal("hello", PyText.Strip("  \t\n hello \r\x1c\x1f "));
        Assert.Equal("hello", PyText.Strip("---hello+++", "-+"));
        Assert.Equal("", PyText.Strip(""));
        Assert.Equal("", PyText.Strip("   "));
        Assert.Equal("", PyText.Strip("", "abc"));
        Assert.True(PyText.IsSpace('\x1d'));
        Assert.False(PyText.IsSpace('a'));
    }

    [Fact]
    public void Length_counts_code_points()
    {
        Assert.Equal(5, PyText.Length("hello"));
        // "𐐀" is supplementary plane (surrogate pair in UTF-16, length 2, but 1 code point)
        Assert.Equal(1, PyText.Length("𐐀"));
        Assert.Equal(6, PyText.Length("hello𐐀"));
    }

    [Fact]
    public void CodePointOrder_orders_by_code_point()
    {
        var comparer = PyText.CodePointOrder;
        Assert.Equal(0, comparer.Compare(null, null));
        Assert.Equal(-1, comparer.Compare(null, "a"));
        Assert.Equal(1, comparer.Compare("a", null));
        Assert.Equal(0, comparer.Compare("abc", "abc"));
        Assert.True(comparer.Compare("abc", "abd") < 0);
        Assert.True(comparer.Compare("abc", "abcd") < 0);
    }

    [Fact]
    public void SplitLines_splits_on_python_line_boundaries()
    {
        var text = "line1\r\nline2\nline3\vline4\fline5\x1cline6\x1dline7\x1eline8\x85line9\u2028line10\u2029line11\n";
        var lines = PyText.SplitLines(text);
        Assert.Equal(["line1", "line2", "line3", "line4", "line5", "line6", "line7", "line8", "line9", "line10", "line11"], lines);
        Assert.Equal(["single"], PyText.SplitLines("single"));
    }

    [Fact]
    public void Prefix_takes_code_points()
    {
        Assert.Equal("hel", PyText.Prefix("hello", 3));
        Assert.Equal("𐐀a", PyText.Prefix("𐐀ab", 2));
        Assert.Equal("hello", PyText.Prefix("hello", 10));
    }
}
