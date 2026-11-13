using System.CommandLine;
using Rig.Cli.Policy;
using Rig.Core.Tests;
using Rig.Core.Tests.Policy;

namespace Rig.Cli.Tests.Policy;

/// <summary><c>rig policy</c>'s output and exit codes; ported from the Python <c>rig.policy.cli</c>. Homes are always temp folders.</summary>
public sealed class PolicyCommandTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _dir.Dispose();
    }

    private string Home => _dir.Folder("home");

    private int RunWith(string policy, params string[] args) =>
        new PolicyCommand(new PolicyHost(policy, Home, _dir.Folder("estate"), false), _out, _err).Build()
            .Parse(args).Invoke(new InvocationConfiguration { Output = _out, Error = _err });

    private int Run(params string[] args) => RunWith(PolicyFiles.Real, args);

    private string[] Lines => _out.ToString().ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void Check_prints_the_tier_and_succeeds_for_an_allowed_command()
    {
        Assert.Equal(0, Run("check", "git status"));
        Assert.Equal("safe: git status\n", _out.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Check_fails_for_a_denied_command()
    {
        Assert.Equal(1, Run("check", "git reset --hard"));
        Assert.Equal("denied: git reset --hard", Lines[0]);
    }

    [Fact]
    public void List_prints_the_skill_count_and_each_rule()
    {
        Assert.Equal(0, Run("list"));

        var policy = PolicyFiles.Load();
        Assert.Equal($"Active skills: {policy.SkillsAllowlist.Count}", Lines[0]);
        Assert.Equal(policy.CommandRules.Count + 1, Lines.Length);
        Assert.Equal("safe     " + policy.CommandRules[0].Pattern, Lines[1]);
    }

    [Fact]
    public void A_malformed_policy_is_reported_not_thrown()
    {
        var path = PolicyFiles.Write(_dir.Path, "[]");

        Assert.Equal(1, RunWith(path, "list"));
        Assert.Equal("rig policy: Malformed command policy: the policy must be an object.\n", _err.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_missing_policy_is_reported_not_thrown()
    {
        Assert.Equal(1, RunWith(Path.Join(_dir.Path, "none.json"), "check", "ls"));
        Assert.StartsWith("rig policy: ", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_tabulates_manifest_commands_and_history_families()
    {
        var root = _dir.Folder("estate");
        File.WriteAllText(_dir.File("estate/web/package.json"), """{"scripts":{"test":"vitest","deploy":"vercel"}}""");
        File.WriteAllText(Path.Join(Home, ".zsh_history"), ": 1:0;git status\n: 2:0;git diff\n: 3:0;pnpm lint\n");

        Assert.Equal(0, Run("audit", "--root", root));

        Assert.Equal(
            [
                "Estate Commands (2)",
                "Project  Command      Risk",
                "web      pnpm deploy  external",
                "web      pnpm test    safe",
                "",
                "Normalized shell-history families:",
                "  git inspection: 2",
                "  pnpm validation: 1",
            ],
            Lines);
    }

    [Fact]
    public void Audit_says_when_history_has_nothing_to_count()
    {
        Assert.Equal(0, Run("audit"));

        Assert.Equal(["Estate Commands (0)", "Project  Command  Risk", "", "Normalized shell-history families:", "  no matching history entries found"], Lines);
    }

    [Fact]
    public void Sync_projects_into_the_given_home_and_reports_each_surface()
    {
        Assert.Equal(0, Run("sync", "-s", "claude", "--system", "AGY", "--home", Home));

        Assert.Equal(
            [
                $"claude: applied — {Path.Join(Home, ".claude", "settings.json")} (Updated native rules.)",
                $"antigravity: applied — {Path.Join(Home, ".gemini", "antigravity-cli", "settings.json")} (Updated native rules.)",
            ],
            Lines);
    }

    [Fact]
    public void A_dry_run_sync_of_every_surface_writes_nothing()
    {
        Assert.Equal(0, Run("sync", "--dry-run"));

        Assert.Equal(4, Lines.Length);
        Assert.All(Lines, l => Assert.Contains(": dry-run — ", l, StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Home));
    }

    [Fact]
    public void Sync_fails_when_a_surface_fails()
    {
        File.WriteAllText(_dir.File("home/.claude/settings.json"), "[1]");

        Assert.Equal(1, Run("sync", "-s", "claude"));
        Assert.Contains("claude: failed — ", Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_rejects_an_unknown_surface()
    {
        Assert.Equal(2, Run("sync", "-s", "claude", "-s", "vim"));
        Assert.Equal(["Unknown CLI system: vim"], Lines);
    }

    [Theory]
    [InlineData(new string[0], new[] { "opencode", "claude", "codex", "antigravity" })]
    [InlineData(new[] { "claude", "all" }, new[] { "opencode", "claude", "codex", "antigravity" })]
    [InlineData(new[] { " Codex ", "agy" }, new[] { "codex", "antigravity" })]
    public void Systems_resolve_aliases_and_all(string[] names, string[] systems)
    {
        Assert.Equal(systems, PolicyCommand.ResolveSystems(names, out var unknown));
        Assert.Null(unknown);
    }
}
