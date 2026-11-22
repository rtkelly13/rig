using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

public sealed class IgnoredTiersTests
{
    [Theory]
    [InlineData("src/App/bin/", IgnoredTier.BuildOutput)]
    [InlineData("src/App/obj/", IgnoredTier.BuildOutput)]
    [InlineData("web/dist/", IgnoredTier.BuildOutput)]
    [InlineData("tsconfig.tsbuildinfo", IgnoredTier.BuildOutput)]
    [InlineData("tests/TestResults/run.trx", IgnoredTier.BuildOutput)]
    [InlineData("x/__pycache__/", IgnoredTier.ToolCache)]
    [InlineData(".ruff_cache/", IgnoredTier.ToolCache)]
    [InlineData("a/b.pyc", IgnoredTier.ToolCache)]
    [InlineData(".DS_Store", IgnoredTier.ToolCache)]
    [InlineData("node_modules/", IgnoredTier.Dependencies)]
    [InlineData("tools/x/.venv/", IgnoredTier.Dependencies)]
    [InlineData("packages/", IgnoredTier.Dependencies)]
    [InlineData("temp/", IgnoredTier.Scratch)]
    [InlineData("temp/run/out.json", IgnoredTier.Scratch)]
    [InlineData("TODO.md", IgnoredTier.Notes)]
    [InlineData(".env", IgnoredTier.LocalConfig)]
    [InlineData("web/.env.production", IgnoredTier.LocalConfig)]
    [InlineData(".claude/settings.local.json", IgnoredTier.LocalConfig)]
    [InlineData("tools/bin/deploy.sh", IgnoredTier.Unknown)]
    [InlineData("secrets/", IgnoredTier.Unknown)]
    public void Ignored_paths_rank_build_output_first_and_personal_files_last(string path, IgnoredTier tier) =>
        Assert.Equal(tier, IgnoredTiers.Of(path).Tier);

    [Fact]
    public void The_tiers_are_numbered_in_cleanup_order_after_none()
    {
        Assert.Equal(
            [IgnoredTier.None, IgnoredTier.BuildOutput, IgnoredTier.ToolCache, IgnoredTier.Dependencies, IgnoredTier.Scratch, IgnoredTier.Notes, IgnoredTier.LocalConfig, IgnoredTier.Unknown],
            Enum.GetValues<IgnoredTier>().OrderBy(tier => (int)tier));
    }

    [Fact]
    public void Every_reclaim_category_but_temp_regenerates_by_name()
    {
        var names = Reclaim.Categories.Where(category => !category.MayHoldWorktrees).SelectMany(category => category.Names);

        Assert.All(names, name => Assert.True(IgnoredTiers.Of(name + "/").Regenerates, name));
    }

    [Fact]
    public void Only_regenerating_rows_may_be_trimmed()
    {
        Assert.All(IgnoredTiers.Table.Where(kind => kind.Trimmed), kind => Assert.True(kind.Class.Regenerates, kind.Name));
    }
}
