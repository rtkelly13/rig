using System.Text.Json;
using Rig.Core.Workload;

namespace Rig.Core.Tests.Workload;

/// <summary>The app list and command-line redaction.</summary>
public sealed class RulesAndRedactionTests
{
    [Fact]
    public void The_bundled_list_names_the_apps_and_one_sessions_app()
    {
        var rules = AppRules.Bundled;

        Assert.Equal("Claude Code", Assert.Single(rules.Apps, rule => rule.Sessions).App);
        Assert.Equal("Claude Code", rules.Match(Row("claude.exe"))!.App);
        Assert.Equal("Claude Code", rules.Match(Row("2.1.3", "/Users/me/.local/share/claude/versions/2.1.3"))!.App);
        Assert.Equal("Claude Desktop", rules.Match(Row("claude.exe", @"C:\Users\me\AppData\Local\AnthropicClaude\app-1\claude.exe"))!.App);
        Assert.Equal("Chrome", rules.Match(Row("Google Chrome Helper (Renderer)", "/Applications/Google Chrome.app/Contents/Frameworks/x"))!.App);
        Assert.Null(rules.Match(Row("python.exe")));
        Assert.True(rules.BurstIgnored("svchost.exe"));
        Assert.True(rules.BurstIgnored("Code Helper (Plugin)"));
        Assert.False(rules.BurstIgnored("python.exe"));
    }

    [Fact]
    public void A_list_without_apps_matches_nothing_and_an_entry_needs_an_app()
    {
        var empty = AppRules.Parse("{}");

        Assert.Empty(empty.Apps);
        Assert.Empty(empty.BurstIgnore);
        Assert.Null(empty.Match(Row("claude")));
        Assert.Throws<InvalidOperationException>(() => AppRules.Parse("""{ "apps": [ { "names": ["x"] } ] }"""));
        Assert.Throws<InvalidOperationException>(() => AppRules.Parse("""{ "apps": [ 1 ] }"""));
        Assert.ThrowsAny<JsonException>(() => AppRules.Parse("not json"));
        Assert.Equal("claude", AppRules.Stem("claude.EXE"));
        Assert.Equal("claude", AppRules.Stem("claude"));
    }

    [Theory]
    [InlineData("git push https://user:hunter2@github.com/x.git", "git push https://***@github.com/x.git")]
    [InlineData("curl -H \"Authorization: Bearer abcdefgh12345\" x", "curl -H \"Authorization: *** ***\" x")]
    [InlineData("gh ghp_abcdefghijklmnopqrstuvwxyz0123", "gh ***")]
    [InlineData("run --api-key=sk-abcdefghijklmnopqrst", "run --api-key=***")]
    [InlineData("env AWS_SECRET_ACCESS_KEY=abc def", "env AWS_SECRET_ACCESS_KEY=*** def")]
    [InlineData("login --password 'two words' --user me", "login --password *** --user me")]
    [InlineData("tool --token abc next", "tool --token *** next")]
    [InlineData("tool --verbose -v", "tool --verbose -v")]
    [InlineData("x 0123456789abcdef0123456789abcdef0123456789 y", "x *** y")]
    [InlineData(@"C:\a\0123456789abcdef0123456789abcdef0123456789\b.dll", @"C:\a\0123456789abcdef0123456789abcdef0123456789\b.dll")]
    [InlineData("  spaced \t out\n ", "spaced out")]
    public void Secrets_are_masked(string command, string safe)
    {
        Assert.Equal(safe, CommandLines.Safe(command, 500));
    }

    [Fact]
    public void Long_commands_are_cut_after_redaction()
    {
        Assert.Equal("abcd…", CommandLines.Safe("abcdefghij", 5));
        Assert.Equal("abcde", CommandLines.Safe("abcde", 5));
        Assert.Equal("…", CommandLines.Safe("abc", 0));
        Assert.Equal("x --token=***", CommandLines.Safe("x --token=0123456789", 13));
    }

    private static ProcessRow Row(string name, string path = "") => new(1, 0, name, path, 0, 0, null);
}
