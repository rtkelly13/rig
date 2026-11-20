using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>Unit tests for the OpenCode global rules injection.</summary>
public sealed class OpencodeRulesTests
{
    [Fact]
    public void A_missing_file_is_created_with_header_and_marker_block()
    {
        var merge = OpencodeRules.Merge(null);

        Assert.Equal(ManagedRulesAction.Created, merge.Action);
        Assert.StartsWith("# Rig Global Rules", merge.Content, StringComparison.Ordinal);
        Assert.Contains(OpencodeRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.EndsWith(OpencodeRules.End + "\n", merge.Content, StringComparison.Ordinal);
        Assert.Contains("rig gh pr wait <PR>", merge.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', merge.Content);
    }

    [Fact]
    public void Existing_user_content_outside_markers_is_preserved_when_block_is_added()
    {
        var existing = "## My OpenCode Notes\n\n- Prefer comments in code.\n";

        var merge = OpencodeRules.Merge(existing);

        Assert.Equal(ManagedRulesAction.Updated, merge.Action);
        Assert.StartsWith("## My OpenCode Notes\n\n- Prefer comments in code.\n\n" + OpencodeRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.Contains(OpencodeRules.ManagedBlock.Trim(), merge.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Merging_twice_is_unchanged()
    {
        var once = OpencodeRules.Merge(null).Content;

        Assert.Equal(new ManagedRulesMerge(once, ManagedRulesAction.Unchanged), OpencodeRules.Merge(once));
    }

    [Fact]
    public void FilePath_is_the_global_agents_file()
    {
        Assert.Equal(Path.Join("/test/home", ".config", "opencode", "AGENTS.md"), OpencodeRules.FilePath("/test/home"));
    }
}
