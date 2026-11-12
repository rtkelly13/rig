using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>Unit tests for Claude Code global rules injection.</summary>
public sealed class ClaudeRulesTests
{
    [Fact]
    public void A_missing_file_is_created_with_header_and_marker_block()
    {
        var merge = ClaudeRules.Merge(null);

        Assert.Equal(ClaudeRulesAction.Created, merge.Action);
        Assert.StartsWith("# Rig Global Rules", merge.Content, StringComparison.Ordinal);
        Assert.Contains(ClaudeRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.EndsWith(ClaudeRules.End + "\n", merge.Content, StringComparison.Ordinal);
        Assert.Contains("rig gh pr land <PR>", merge.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', merge.Content);
    }

    [Fact]
    public void Existing_user_content_outside_markers_is_preserved_when_block_is_added()
    {
        var existing = "## My Custom Claude Rules\n\n- Custom rule 1\n";

        var merge = ClaudeRules.Merge(existing);

        Assert.Equal(ClaudeRulesAction.Updated, merge.Action);
        Assert.StartsWith("## My Custom Claude Rules\n\n- Custom rule 1\n\n" + ClaudeRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.Contains(ClaudeRules.ManagedBlock.Trim(), merge.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_markers_are_updated_while_content_before_and_after_is_kept()
    {
        var before = "## Other Tool Config\n\nTool rule\n\n";
        var after = "\n## Personal Note\n\nNote text\n";
        var existing = before + ClaudeRules.Begin + "\nOld content\n" + ClaudeRules.End + after;

        var merge = ClaudeRules.Merge(existing);

        Assert.Equal(ClaudeRulesAction.Updated, merge.Action);
        Assert.Equal(before + ClaudeRules.Render() + "\n## Personal Note\n\nNote text\n", merge.Content);
    }

    [Fact]
    public void Merging_twice_is_unchanged()
    {
        var once = ClaudeRules.Merge(null).Content;

        Assert.Equal(new ClaudeRulesMerge(once, ClaudeRulesAction.Unchanged), ClaudeRules.Merge(once));
    }

    [Fact]
    public void FilePath_is_under_dot_claude_rules()
    {
        var path = ClaudeRules.FilePath("/test/home");

        Assert.Equal(Path.Combine("/test/home", ".claude", "rules", "rig.md"), path);
    }
}
