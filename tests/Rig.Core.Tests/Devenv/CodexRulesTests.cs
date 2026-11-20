using Rig.Core.Devenv;

namespace Rig.Core.Tests.Devenv;

/// <summary>Unit tests for the Codex global context injection.</summary>
public sealed class CodexRulesTests
{
    [Fact]
    public void A_missing_file_is_created_with_header_and_marker_block()
    {
        var merge = CodexRules.Merge(null);

        Assert.Equal(ManagedRulesAction.Created, merge.Action);
        Assert.StartsWith("# Rig Global Rules", merge.Content, StringComparison.Ordinal);
        Assert.Contains(CodexRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.EndsWith(CodexRules.End + "\n", merge.Content, StringComparison.Ordinal);
        Assert.Contains("rig gh pr land <PR>", merge.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', merge.Content);
    }

    [Fact]
    public void Existing_user_content_outside_markers_is_preserved_when_block_is_added()
    {
        var existing = "## My Codex Notes\n\n- Keep answers short.\n";

        var merge = CodexRules.Merge(existing);

        Assert.Equal(ManagedRulesAction.Updated, merge.Action);
        Assert.StartsWith("## My Codex Notes\n\n- Keep answers short.\n\n" + CodexRules.Begin, merge.Content, StringComparison.Ordinal);
        Assert.Contains(CodexRules.ManagedBlock.Trim(), merge.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Merging_twice_is_unchanged()
    {
        var once = CodexRules.Merge(null).Content;

        Assert.Equal(new ManagedRulesMerge(once, ManagedRulesAction.Unchanged), CodexRules.Merge(once));
    }

    [Fact]
    public void FilePath_and_override_are_under_dot_codex()
    {
        Assert.Equal(Path.Join("/test/home", ".codex", "AGENTS.md"), CodexRules.FilePath("/test/home"));
        Assert.Equal(Path.Join("/test/home", ".codex", "AGENTS.override.md"), CodexRules.OverridePath("/test/home"));
    }
}
