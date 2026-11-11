using Rig.Core.Estate;

namespace Rig.Core.Tests.Estate;

/// <summary>The estate's pure pieces: sizes, path order, porcelain and ref parsing, origin matching.</summary>
public sealed class EstateParsingTests
{
    [Theory]
    [InlineData(0, "0B")]
    [InlineData(1023, "1023B")]
    [InlineData(1024, "1.0KB")]
    [InlineData(1536, "1.5KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0GB")]
    [InlineData(3L * 1024 * 1024 * 1024 * 1024, "3.0TB")]
    public void Human_formats_like_python(long size, string expected) => Assert.Equal(expected, Bytes.Human(size));

    [Fact]
    public void HumanToGb_stops_at_gigabytes() => Assert.Equal("3072.0GB", Bytes.HumanToGb(3L * 1024 * 1024 * 1024 * 1024));

    [Theory]
    [InlineData("10GB", 10L * 1024 * 1024 * 1024)]
    [InlineData("1.5 MB", 1572864)]
    [InlineData("2kb", 2048)]
    [InlineData("512", 512)]
    [InlineData("nonsense", 0)]
    public void ParseSize_reads_suffixes(string text, long expected) => Assert.Equal(expected, Bytes.ParseSize(text));

    [Fact]
    public void Paths_sort_by_segment_not_by_string()
    {
        string[] paths = ["/a/foo-x", "/a/foo/bar", "/a/Foo", "/a"];
        Assert.Equal(["/a", "/a/Foo", "/a/foo/bar", "/a/foo-x"], [.. paths.Order(PathOrder.Instance)]);
    }

    [Fact]
    public void Worktree_porcelain_parses_every_field_and_marks_the_first_main()
    {
        const string porcelain = """
            worktree /r
            HEAD abc
            branch refs/heads/main

            worktree /r/temp/a
            HEAD 0000000000
            branch refs/heads/gone
            locked held by me

            worktree /r/temp/b
            HEAD def
            detached
            prunable gitdir file points to non-existent location

            """;

        var trees = Worktree.Parse(porcelain);

        Assert.Equal(3, trees.Count);
        Assert.True(trees[0].IsMain);
        Assert.Equal("main", trees[0].Branch);
        Assert.False(trees[0].Unborn);
        Assert.True(trees[1].Unborn);
        Assert.True(trees[1].Locked);
        Assert.Equal("held by me", trees[1].LockReason);
        Assert.True(trees[2].Detached);
        Assert.True(trees[2].Prunable);
        Assert.Equal("gitdir file points to non-existent location", trees[2].PruneReason);
        Assert.False(trees[2].IsMain);
    }

    [Fact]
    public void Branches_parse_upstream_gone_current_and_elsewhere()
    {
        const string refs = "refs/heads/main\trefs/remotes/origin/main\t\nrefs/heads/old\trefs/remotes/origin/old\t[gone]\nrefs/heads/wt\t\t\n\n";

        var branches = LocalBranch.Parse(refs, "main", new HashSet<string> { "wt", "main" });

        Assert.Equal(new LocalBranch("main", "origin/main", false, true, false), branches[0]);
        Assert.Equal(new LocalBranch("old", "origin/old", true, false, false), branches[1]);
        Assert.Equal(new LocalBranch("wt", string.Empty, false, false, true), branches[2]);
    }

    [Theory]
    [InlineData("git@github.com:o/r.git", "github.com/o/r")]
    [InlineData("https://github.com/o/r", "github.com/o/r")]
    [InlineData("ssh://git@github.com/o/r.git", "github.com/o/r")]
    [InlineData("http://host/o/r", "host/o/r")]
    public void Origins_normalise_to_host_owner_name(string remote, string expected) => Assert.Equal(expected, DuplicateClone.Normalise(remote));

    [Fact]
    public void Duplicate_clones_group_by_origin_and_ignore_singletons()
    {
        Exposure[] exposures =
        [
            new("/c/b", 2, Origin: "git@github.com:o/r.git"),
            new("/c/a", 1, Origin: "https://github.com/o/r"),
            new("/c/solo", 5, Origin: "https://github.com/o/solo"),
            new("/c/none", 3),
        ];

        var duplicate = Assert.Single(DuplicateClone.Find(exposures));

        Assert.Equal("github.com/o/r", duplicate.Origin);
        Assert.Equal(["/c/b", "/c/a"], duplicate.Repos);
        Assert.Equal([new("b", 2), new KeyValuePair<string, int>("a", 1)], duplicate.LocalOnly);
    }

    [Fact]
    public void Exposure_scores_commits_over_dirt()
    {
        var exposure = new Exposure("/r", LocalOnlyCommits: 1, DirtyFiles: 2, Stashes: 1);

        Assert.Equal(21, exposure.Score);
        Assert.True(exposure.AtRisk);
        Assert.False(new Exposure("/r").AtRisk);
        Assert.Empty(new Exposure("/r").Branches);
    }
}
