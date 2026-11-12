using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>Unit tests for AGY global context injection.</summary>
public sealed class AgyRulesTests
{
    [Fact]
    public void A_missing_file_is_created_with_the_marker_block()
    {
        var merge = AgyRules.Merge(null);

        Assert.Equal(AgyRulesAction.Created, merge.Action);
        Assert.StartsWith(AgyRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.EndsWith(AgyRules.End + "\n", merge.Content, StringComparison.Ordinal);
        Assert.Contains("rig gh pr land <PR>", merge.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', merge.Content);
    }

    [Fact]
    public void Existing_user_content_outside_markers_is_preserved_when_block_is_added()
    {
        var existing = "## User Rules\n\nRule 1: Never do bad things.\n";

        var merge = AgyRules.Merge(existing);

        Assert.Equal(AgyRulesAction.Updated, merge.Action);
        Assert.StartsWith("## User Rules\n\nRule 1: Never do bad things.\n\n" + AgyRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.Contains(AgyRules.ManagedBlock.Trim(), merge.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Existing_markers_are_updated_while_content_before_and_after_is_kept()
    {
        var before = "## Keep Notes\n\nKeep note 1\n\n";
        var after = "\n## Other Tool\n\nTool rules\n";
        var existing = before + AgyRules.Begin + "\nOld content\n" + AgyRules.End + after;

        var merge = AgyRules.Merge(existing);

        Assert.Equal(AgyRulesAction.Updated, merge.Action);
        Assert.Equal(before + AgyRules.Render() + "\n## Other Tool\n\nTool rules\n", merge.Content);
    }

    [Fact]
    public void Merging_twice_is_unchanged()
    {
        var once = AgyRules.Merge(null).Content;

        Assert.Equal(new AgyRulesMerge(once, AgyRulesAction.Unchanged), AgyRules.Merge(once));
    }
}
