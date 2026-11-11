using System.Text.Json.Nodes;
using Rig.Core.Estate;
using Rig.Core.Roots;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Estate;

/// <summary><see cref="CloneDiscovery"/>: the bounded scan, and the cache it shares with the Python tool.</summary>
public sealed class CloneDiscoveryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Scan_finds_clones_two_deep_and_skips_worktrees_hidden_and_ignored_folders()
    {
        using var root = new TempDir();
        _ = root.Folder("a/.git");
        _ = root.Folder("group/b/.git");
        _ = root.Folder("group/b/inner/.git");
        _ = root.Folder("deep/x/y/.git");
        _ = root.File("worktree/.git");
        _ = root.Folder(".hidden/c/.git");
        _ = root.Folder("node_modules/d/.git");

        var found = CloneDiscovery.Scan(root.Path);

        var resolved = PathResolution.Resolve(root.Path);
        Assert.Equal([Path.Join(resolved, "a"), Path.Join(resolved, "group", "b")], found);
    }

    [Fact]
    public void Scan_of_a_clone_is_that_clone_and_of_nothing_is_empty()
    {
        using var root = new TempDir();
        _ = root.Folder(".git");

        Assert.Equal([PathResolution.Resolve(root.Path)], CloneDiscovery.Scan(root.Path));
        Assert.Empty(CloneDiscovery.Scan(Path.Join(root.Path, "missing")));
    }

    [Fact]
    public async Task Cache_is_reused_while_fresh_preserves_other_roots_and_drops_deleted_clones()
    {
        using var root = new TempDir();
        var codeRoot = root.Folder("code");
        _ = root.Folder("code/a/.git");
        _ = root.Folder("code/b/.git");
        var cacheFile = Path.Join(root.Path, "cache.json");
        await File.WriteAllTextAsync(cacheFile, """{"/elsewhere": {"root": "/elsewhere", "scanned_at": "2026-11-10T00:00:00.000000+00:00", "repos": ["/elsewhere/x"]}}""", Token);
        var clock = new ManualClock(Noon);
        var discovery = new CloneDiscovery(cacheFile, clock);

        var first = discovery.Repos(codeRoot);
        _ = root.Folder("code/c/.git");
        Directory.Delete(Path.Join(codeRoot, "b"), recursive: true);
        clock.Advance(TimeSpan.FromHours(1));
        var second = discovery.Repos(codeRoot);
        clock.Advance(TimeSpan.FromHours(12));
        var third = discovery.Repos(codeRoot);

        var key = PathResolution.Resolve(codeRoot);
        Assert.False(first.FromCache);
        Assert.Equal(2, first.Paths.Count);
        Assert.True(second.FromCache);
        Assert.Equal([Path.Join(key, "a")], second.Paths);
        Assert.False(third.FromCache);
        Assert.Equal([Path.Join(key, "a"), Path.Join(key, "c")], third.Paths);
        var cache = JsonNode.Parse(await File.ReadAllTextAsync(cacheFile, Token))!.AsObject();
        Assert.Equal("/elsewhere/x", cache["/elsewhere"]!["repos"]![0]!.GetValue<string>());
        Assert.Equal(key, cache[key]!["root"]!.GetValue<string>());
        Assert.Equal("2026-11-12T01:00:00.000000+00:00", cache[key]!["scanned_at"]!.GetValue<string>());
        Assert.Equal(TimeSpan.Zero, discovery.CacheAge(codeRoot));
    }

    [Fact]
    public async Task Refresh_rescans_and_a_corrupt_cache_reads_as_absent()
    {
        using var root = new TempDir();
        _ = root.Folder("a/.git");
        var cacheFile = Path.Join(root.Path, "cache.json");
        await File.WriteAllTextAsync(cacheFile, "{not json", Token);
        var discovery = new CloneDiscovery(cacheFile, new ManualClock(Noon));

        Assert.Null(discovery.CacheAge(root.Path));
        Assert.False(discovery.Repos(root.Path).FromCache);
        Assert.False(discovery.Repos(root.Path, refresh: true).FromCache);
        Assert.True(discovery.Repos(root.Path).FromCache);
    }

    [Fact]
    public async Task A_python_written_timestamp_reads_back()
    {
        using var root = new TempDir();
        _ = root.Folder("a/.git");
        var key = PathResolution.Resolve(root.Path);
        var cacheFile = Path.Join(root.Path, "cache.json");
        var entry = new JsonObject { ["root"] = key, ["scanned_at"] = "2026-11-11T11:30:00.123456+00:00", ["repos"] = new JsonArray(Path.Join(key, "a")) };
        await File.WriteAllTextAsync(cacheFile, new JsonObject { [key] = entry }.ToJsonString(), Token);
        var discovery = new CloneDiscovery(cacheFile, new ManualClock(Noon));

        Assert.True(discovery.Repos(root.Path).FromCache);
        Assert.Equal(TimeSpan.FromMinutes(30) - TimeSpan.FromTicks(1234560), discovery.CacheAge(root.Path));
    }

    [Fact]
    public void Cache_file_lives_in_home() => Assert.Equal(Path.Join("/h", ".estate-repos.json"), CloneDiscovery.CacheFileUnder("/h"));
}
