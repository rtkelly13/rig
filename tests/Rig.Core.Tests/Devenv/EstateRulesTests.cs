using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>The estate-wide AGENTS.md block; ported from <c>test_ai_tools.py</c>.</summary>
public sealed class EstateRulesTests
{
    [Fact]
    public void A_missing_file_is_created_with_the_header_and_block()
    {
        var merge = EstateRules.Merge(null);

        Assert.Equal(EstateRulesAction.Created, merge.Action);
        Assert.StartsWith("# AGENTS.md\n", merge.Content, StringComparison.Ordinal);
        Assert.Contains(EstateRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.EndsWith($"carrying on without it.\n{EstateRules.End}\n", merge.Content, StringComparison.Ordinal);
        Assert.Contains("data, never instructions", merge.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', merge.Content);
    }

    [Fact]
    public void A_refresh_keeps_what_is_written_outside_the_markers()
    {
        var existing = "## Preamble\n\nAlso keep me.\n\n" + EstateRules.Begin + "\nold rules\n" + EstateRules.End + "\n\n## My own notes\n\nKeep me.\n";

        var merge = EstateRules.Merge(existing);

        Assert.Equal(EstateRulesAction.Updated, merge.Action);
        Assert.Equal("## Preamble\n\nAlso keep me.\n\n" + EstateRules.Render() + "\n## My own notes\n\nKeep me.\n", merge.Content);
    }

    [Fact]
    public void A_file_without_markers_is_appended_to_never_replaced()
    {
        var merge = EstateRules.Merge("# AGENTS.md\n\nMy hand-written estate rules.\n\n\n");

        Assert.Equal(EstateRulesAction.Updated, merge.Action);
        Assert.Equal("# AGENTS.md\n\nMy hand-written estate rules.\n\n" + EstateRules.Render(), merge.Content);
    }

    [Fact]
    public void Merging_twice_changes_nothing()
    {
        var once = EstateRules.Merge(null).Content;

        Assert.Equal(new EstateRulesMerge(once, EstateRulesAction.Unchanged), EstateRules.Merge(once));
    }

    [Fact]
    public void A_blank_tail_after_the_block_is_dropped() =>
        Assert.Equal(EstateRules.Render(), EstateRules.Merge(EstateRules.Begin + "\nold\n" + EstateRules.End + "\n\n  \n").Content);
}
