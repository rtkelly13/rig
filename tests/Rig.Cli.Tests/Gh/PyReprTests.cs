using Rig.Cli.Gh.Threads;

namespace Rig.Cli.Tests.Gh;

/// <summary>The expected values are what Python's <c>repr()</c> prints for each input.</summary>
public sealed class PyReprTests
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
    public void Matches_python_repr(string text, string expected) => Assert.Equal(expected, PyRepr.Of(text));
}
