using Rig.Core.Policy;

namespace Rig.Core.Tests.Policy;

/// <summary>Ported from the classification and loading cases of the Python <c>tests/test_policy.py</c>.</summary>
public sealed class CommandPolicyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("uv run pytest tests/test_policy.py", CommandRisk.Safe)]
    [InlineData("pnpm build", CommandRisk.Local)]
    [InlineData("pnpm test:watch", CommandRisk.Local)]
    [InlineData("pnpm publish", CommandRisk.External)]
    [InlineData("git push origin main", CommandRisk.Safe)]
    [InlineData("git fetch --all", CommandRisk.Safe)]
    [InlineData("git branch -a", CommandRisk.Safe)]
    [InlineData("git show HEAD", CommandRisk.Safe)]
    [InlineData("git rev-parse --show-toplevel", CommandRisk.Safe)]
    [InlineData("git worktree list", CommandRisk.Safe)]
    [InlineData("git reset --hard HEAD", CommandRisk.Denied)]
    [InlineData("git branch -D old-branch", CommandRisk.Denied)]
    [InlineData("unknown-tool do-something", CommandRisk.Denied)]
    [InlineData("git   status    -s", CommandRisk.Safe)]
    public void Classifies_validation_and_dangerous_commands(string command, CommandRisk risk) =>
        Assert.Equal(risk, PolicyFiles.Load().Check(command));

    [Theory]
    [InlineData("uv run pytest tests && git push")]
    [InlineData("uv run pytest $(printf hidden)")]
    [InlineData("git log `whoami`")]
    [InlineData("git status ; ls")]
    [InlineData("git status | wc")]
    [InlineData("git status > out")]
    [InlineData("git log \"unclosed")]
    [InlineData("git log '|'")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_composed_or_unparseable_line_is_denied_before_matching(string command) =>
        Assert.Equal(CommandRisk.Denied, PolicyFiles.Load().Check(command));

    [Theory]
    [InlineData("git log # | pipe in a comment")]
    public void Commented_punctuation_is_not_a_control_token(string command) =>
        Assert.True(CommandPolicy.IsDirect(command));

    [Theory]
    [InlineData("find . -delete")]
    [InlineData("find -delete .")]
    [InlineData("find . -name tmp -delete")]
    [InlineData("find . -exec rm scratch {}")]
    [InlineData("find . -execdir sh {} +")]
    [InlineData("find -name scratch -exec cat {} +")]
    [InlineData("find . -ok rm scratch {}")]
    [InlineData("sort -o out.txt in.txt")]
    [InlineData("sort in.txt -o out.txt")]
    [InlineData("git reflog expire --expire=now --all")]
    [InlineData("git reflog delete HEAD@{0}")]
    [InlineData("agent-browser eval document.cookie")]
    [InlineData("agent-browser run --session s eval window.fetch")]
    [InlineData("agent-browser connect 9222")]
    [InlineData("agent-browser run connect https://attacker.example")]
    [InlineData("gh pr merge 42 --admin --auto --squash")]
    public void Mutating_flags_are_denied_even_under_read_only_wildcards(string command) =>
        Assert.Equal(CommandRisk.Denied, PolicyFiles.Load().Check(command));

    [Theory]
    [InlineData("find . -name policy.py")]
    [InlineData("find . -maxdepth 2 -type d")]
    [InlineData("sort in.txt")]
    [InlineData("git reflog show HEAD")]
    [InlineData("git reflog HEAD")]
    [InlineData("agent-browser run open https://example.com")]
    [InlineData("agent-browser run click @e1")]
    [InlineData("agent-browser run snapshot -i")]
    [InlineData("gh pr ready 42")]
    [InlineData("gh pr merge 42 --auto --squash")]
    [InlineData("gh pr merge 42 --disable-auto")]
    [InlineData("gh stack merge 42 --yes")]
    [InlineData("gh stack merge 600 --yes --squash")]
    [InlineData("gh stack sync")]
    [InlineData("gh stack sync --remote origin")]
    [InlineData("gh run rerun 1234567890")]
    public void Read_only_and_merge_requested_commands_stay_safe(string command) =>
        Assert.Equal(CommandRisk.Safe, PolicyFiles.Load().Check(command));

    [Theory]
    [InlineData("gh pr merge --admin 42")]
    [InlineData("gh pr merge 42 --squash --admin")]
    [InlineData("gh pr merge 42 --squash")]
    [InlineData("gh stack merge 42")]
    [InlineData("gh stack sync --prune")]
    [InlineData("gh stack sync --remote origin --prune")]
    public void Merges_without_the_merge_request_flags_are_not_safe(string command) =>
        Assert.NotEqual(CommandRisk.Safe, PolicyFiles.Load().Check(command));

    [Fact]
    public void On_Windows_a_command_matches_regardless_of_case()
    {
        var policy = CommandPolicy.Load(PolicyFiles.Real, windows: true);

        Assert.Equal(CommandRisk.Safe, policy.Check("GIT STATUS"));
        Assert.Equal(CommandRisk.Denied, PolicyFiles.Load().Check("GIT STATUS"));
    }

    [Fact]
    public void The_stricter_tier_wins_over_a_more_specific_rule()
    {
        var policy = new CommandPolicy([], [new("git *", CommandRisk.Local), new("git status*", CommandRisk.Safe), new("git push*", CommandRisk.External)], windows: false);

        Assert.Equal(CommandRisk.Local, policy.Check("git status"));
        Assert.Equal(CommandRisk.External, policy.Check("git push origin"));
    }

    [Fact]
    public void A_policy_file_accepts_bare_and_annotated_rules_ordered_safe_to_denied()
    {
        var path = PolicyFiles.Write(_dir.Path, """
            {"skills": {"allow": ["one"]},
             "commands": {"denied": [{"pattern": "rm*", "exclude_systems": ["claude"]}],
                          "safe": ["uv run help", {"pattern": "uv run pytest*", "rationale": "Tests."}]}}
            """);

        var policy = CommandPolicy.Load(path, windows: false);

        Assert.Equal(["one"], policy.SkillsAllowlist);
        Assert.Equal(
            [("uv run help", "", CommandRisk.Safe), ("uv run pytest*", "Tests.", CommandRisk.Safe), ("rm*", "", CommandRisk.Denied)],
            policy.CommandRules.Select(r => (r.Pattern, r.Rationale, r.Risk)));
        Assert.Equal(["claude"], policy.CommandRules[2].ExcludeSystems);
    }

    [Fact]
    public void Skills_can_come_from_a_separate_allowlist_file()
    {
        File.WriteAllText(Path.Join(_dir.Path, "skills.json"), """["a", "b"]""");
        var path = PolicyFiles.Write(_dir.Path, """{"skills": {"allowlist_file": "skills.json"}, "commands": {}}""");

        Assert.Equal(["a", "b"], CommandPolicy.Load(path, windows: false).SkillsAllowlist);
    }

    [Fact]
    public void A_policy_without_skills_has_none()
    {
        var path = PolicyFiles.Write(_dir.Path, """{"skills": {}, "commands": {"safe": []}}""");

        Assert.Empty(CommandPolicy.Load(path, windows: false).SkillsAllowlist);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{"skills": {"allow": []}}""")]
    [InlineData("""{"commands": {"forbidden": ["git push*"]}}""")]
    [InlineData("""{"commands": {"denied": "git push*"}}""")]
    [InlineData("""{"commands": {"denied": [{"rationale": "No pattern."}]}}""")]
    [InlineData("""{"commands": {"denied": [3]}}""")]
    [InlineData("""{"commands": {"denied": [{"pattern": "x", "rationale": 1}]}}""")]
    [InlineData("""{"commands": {"denied": [{"pattern": "x", "exclude_systems": "claude"}]}}""")]
    [InlineData("""{"skills": [], "commands": {}}""")]
    [InlineData("""{"skills": {"allow": [1]}, "commands": {}}""")]
    [InlineData("""{"skills": {"allowlist_file": 1}, "commands": {}}""")]
    public void A_misshapen_policy_fails_closed(string json)
    {
        // A dropped deny rule would let a broader allow rule match, so a bad rule is an error.
        var path = PolicyFiles.Write(_dir.Path, json);

        var error = Assert.Throws<InvalidDataException>(() => CommandPolicy.Load(path, windows: false));
        Assert.StartsWith("Malformed command policy: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allowlist_file_that_is_not_a_list_fails_closed()
    {
        File.WriteAllText(Path.Join(_dir.Path, "skills.json"), """{"a": 1}""");
        var path = PolicyFiles.Write(_dir.Path, """{"skills": {"allowlist_file": "skills.json"}, "commands": {}}""");

        _ = Assert.Throws<InvalidDataException>(() => CommandPolicy.Load(path, windows: false));
    }
}
