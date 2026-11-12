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
}
