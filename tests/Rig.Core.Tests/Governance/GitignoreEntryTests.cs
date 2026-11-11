using Rig.Core.Governance;

namespace Rig.Core.Tests.Governance;

/// <summary>When a <c>.gitignore</c> satisfies an entry, and what is appended when it does not. Ported from <c>tests/test_gitignore_manager.py</c>.</summary>
public sealed class GitignoreEntryTests
{
    private static GitignoreEntry Any(params string[] write) => new("e", "Comment", write, write);

    private static GitignoreEntry All(params string[] write) => new("e", "Comment", write, write, MatchAll: true);

    [Theory]
    [InlineData("bin/", "bin")]
    [InlineData("bin/", "/bin/")]
    [InlineData("bin/", "**/bin/")]
    [InlineData("/temp", "temp/")]
    [InlineData("temp/", ".temp")]
    [InlineData("bin/", "[Bb]in")]
    [InlineData("obj/", "**/obj")]
    [InlineData(".venv/", "ENV/")]
    [InlineData("node_modules/", "**/node_modules")]
    [InlineData("TODO.md", "[Tt][Oo][Dd][Oo].md")]
    [InlineData(".DS_Store", "*/.DS_Store")]
    [InlineData("dist/", "dist/   # build output")]
    public void A_common_spelling_satisfies_the_token(string token, string line) =>
        Assert.Empty(Any(token).MissingLines($"# header\n{line}\n"));

    [Theory]
    [InlineData("bin/", "binaries/")]
    [InlineData("temp/", "# temp/")]
    [InlineData("TODO.md", "notes.md")]
    [InlineData(".DS_Store", ".DS_Store_old")]
    public void A_different_or_commented_line_does_not(string token, string line) =>
        Assert.Equal([token], Any(token).MissingLines(line));

    [Fact]
    public void Any_is_met_by_one_satisfied_token_and_otherwise_wants_every_line()
    {
        var entry = new GitignoreEntry("venv", "Envs", [".venv/", "venv/"], ["venv"]);

        Assert.Empty(entry.MissingLines("venv\n"));
        Assert.Equal([".venv/", "venv/"], entry.MissingLines("other\n"));
    }

    [Fact]
    public void All_wants_only_the_unsatisfied_lines()
    {
        Assert.Equal(["todo.md"], All("TODO.md", "todo.md").MissingLines("TODO.md\n"));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\f")]
    [InlineData("\u2028")]
    [InlineData("\x85")]
    public void Lines_split_where_python_split_them(string separator) =>
        Assert.Empty(All("a/", "b/").MissingLines($"a/{separator}b/"));

    [Fact]
    public void The_missing_block_is_a_blank_line_the_comment_and_the_lines()
    {
        Assert.Equal("\n# Comment\na\nb\n", Any("a").RenderMissing(["a", "b"]));
        Assert.Equal(string.Empty, Any("a").RenderMissing([]));
    }
}
