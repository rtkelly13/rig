using System.Text.Json;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Packages;

/// <summary><see cref="PackageDashboard"/>. Ported from <c>tests/test_workspace_package_dashboard.py</c>.</summary>
public sealed class PackageDashboardTests : IDisposable
{
    private readonly TempDir _root = new();
    private readonly FakeProc _proc = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => _root.Dispose();

    [Fact]
    public void An_unreleased_section_with_entries_has_unreleased_changes() =>
        Assert.True(PackageDashboard.ChangelogHasUnreleased("""
            # Changelog

            All notable changes documented here.

            ## [Unreleased]

            ### Added
            - Iceberg REST client retry policies (#123)

            ## [0.1.0] - 2026-10-01
            """));

    [Theory]
    [InlineData("# Changelog\n\n## [Unreleased]\n\n## [0.0.1-alpha.1] - 2026-10-25\n\nFirst published build.\n")]
    [InlineData("# Changelog\n\n## [Unreleased]\n\n<!-- Upcoming changes go here -->\n\n## [1.0.0] - 2026-05-01\n")]
    [InlineData("# Changelog\n\n## [Unreleased]\n\n### Added\n---\n\n## [1.0.0]\n")]
    [InlineData("# Changelog\n\n## 0.16.0\n\nFive MDX figures added.\n\n## 0.15.0\n")]
    public void Empty_comment_only_or_missing_unreleased_sections_are_clean(string changelog) =>
        Assert.False(PackageDashboard.ChangelogHasUnreleased(changelog));

    [Fact]
    public async Task The_changelog_beside_the_manifest_wins_over_the_root_one()
    {
        Directory.CreateDirectory(_root.Folder("Repo/src"));
        await File.WriteAllTextAsync(_root.File("Repo/CHANGELOG.md"), "## [Unreleased]\n- root entry\n", Token);
        await File.WriteAllTextAsync(_root.File("Repo/src/CHANGELOG.md"), "## [Unreleased]\n", Token);

        Assert.Equal(new ChangelogState(true, false), PackageDashboard.ReadChangelog(_root.Folder("Repo"), "src/Pkg.csproj"));
        Assert.Equal(new ChangelogState(true, true), PackageDashboard.ReadChangelog(_root.Folder("Repo"), "Other/Pkg.csproj"));
        Assert.Equal(new ChangelogState(false, false), PackageDashboard.ReadChangelog(_root.Folder("Missing"), "x"));
    }

    [Fact]
    public void Package_scoped_tags_win_over_global_ones()
    {
        string[] tags = ["Iceberg.Net.Core/v0.0.1-alpha.1", "Iceberg.Net.Core/v0.0.2", "Iceberg.Net.Avro/v0.0.1", "v0.0.0-root"];

        Assert.Equal(
            [new("Iceberg.Net.Core/v0.0.1-alpha.1", "0.0.1-alpha.1"), new MatchedTag("Iceberg.Net.Core/v0.0.2", "0.0.2")],
            PackageDashboard.FindMatchingTags(tags, "Iceberg.Net.Core"));
    }

    [Fact]
    public void Global_tags_are_the_fallback_and_non_version_tags_are_ignored()
    {
        string[] tags = ["v0.0.1", "v0.0.2", "prerebase/360-guard", "random-tag", "vnext"];

        Assert.Equal([new("v0.0.1", "0.0.1"), new MatchedTag("v0.0.2", "0.0.2")], PackageDashboard.FindMatchingTags(tags, "Parquet.SourceGenerator"));
        Assert.Empty(PackageDashboard.FindMatchingTags(["branch-alpha", "prerebase/ci-matrix"], "Parquet.TypeProvider"));
    }

    [Fact]
    public async Task Packages_released_and_tagged_at_one_version_are_synced()
    {
        var rows = await Collect(
            "Iceberg.NET",
            ["v0.0.1-alpha.1"],
            new() { ["Iceberg.Net.Core"] = Published("0.0.1-alpha.1"), ["Iceberg.Net.Avro"] = Published("0.0.1-alpha.1") },
            "Iceberg.Net.Core",
            "Iceberg.Net.Avro");

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(("0.0.1-alpha.1", "v0.0.1-alpha.1", "0.0.1-alpha.1"), (row.RegistryVersion, row.LatestTag, row.TagVersion));
            Assert.Empty(row.Flags);
            Assert.Equal("synced", row.Status);
        });
    }

    [Fact]
    public async Task A_registry_version_without_a_tag_is_flagged()
    {
        var info = Published("0.1.0-alpha.1") with { AllVersions = ["0.0.1", "0.0.2", "0.0.3", "0.1.0-alpha.1"] };

        var row = Assert.Single(await Collect("Repo", ["v0.0.1", "v0.0.3", "v0.0.2"], new() { ["Pkg"] = info }, "Pkg"));

        Assert.Equal("v0.0.3", row.LatestTag);
        Assert.Equal("registry-without-tag", row.Status);
    }

    [Fact]
    public async Task A_package_with_no_release_and_no_tag_is_unpublished()
    {
        var row = Assert.Single(await Collect("Repo", [], [], "Pkg"));

        Assert.Equal(((string?)null, (string?)null, "unpublished"), (row.RegistryVersion, row.LatestTag, row.Status));
        Assert.Empty(row.Flags);
        Assert.False(row.ChangelogFound);
    }

    [Fact]
    public async Task Packages_of_one_project_at_different_versions_drift()
    {
        var rows = await Collect(
            "Monorepo", ["Pkg.A/v1.0.0", "Pkg.B/v0.9.0"], new() { ["Pkg.A"] = Published("1.0.0"), ["Pkg.B"] = Published("0.9.0") }, "Pkg.A", "Pkg.B");

        Assert.All(rows, row => Assert.Equal(["version-family-drift"], row.Flags));
    }

    [Fact]
    public async Task An_unreadable_registry_is_flagged_and_is_not_drift()
    {
        var rows = await Collect(
            "Monorepo", ["Pkg.A/v1.0.0", "Pkg.B/v1.0.0"], new() { ["Pkg.A"] = Published("1.0.0"), ["Pkg.B"] = RegistryInfo.LookupFailed }, "Pkg.A", "Pkg.B");

        Assert.Equal([[], ["registry-unavailable"]], rows.Select(row => row.Flags));
        Assert.Equal("registry-unavailable", rows[1].Status);
    }

    [Fact]
    public async Task Unreleased_changelog_entries_are_flagged()
    {
        Directory.CreateDirectory(_root.Folder("Repo"));
        await File.WriteAllTextAsync(_root.File("Repo/CHANGELOG.md"), "## [Unreleased]\n- a fix\n", Token);

        var row = Assert.Single(await Collect("Repo", ["v1.0.0"], new() { ["@scope/pkg"] = Published("1.0.0") }, "@scope/pkg"));

        Assert.True(row.ChangelogUnreleased);
        Assert.Equal(["unreleased-changes"], row.Flags);
    }

    [Fact]
    public async Task Unlisted_and_deprecated_releases_are_flagged()
    {
        var info = Published("2.0.0") with { Unlisted = true, Deprecated = true };

        var row = Assert.Single(await Collect("Repo", ["v2.0.0"], new() { ["OldPkg"] = info }, "OldPkg"));

        Assert.Equal("unlisted, deprecated", row.Status);
        Assert.True(row.Unlisted && row.Deprecated);
    }

    [Fact]
    public async Task A_tag_ahead_of_the_registry_is_flagged()
    {
        var row = Assert.Single(await Collect("Repo", ["v1.0.0", "v1.1.0"], new() { ["my-pkg"] = Published("1.0.0") }, "my-pkg"));

        Assert.Equal("v1.1.0", row.LatestTag);
        Assert.Equal(["tag-without-registry"], row.Flags);
    }

    [Fact]
    public async Task Clones_are_found_under_the_root_its_group_folder_or_the_code_root()
    {
        Directory.CreateDirectory(_root.Folder("root/Direct"));
        Directory.CreateDirectory(_root.Folder("root/labs/Grouped"));
        Directory.CreateDirectory(_root.Folder("code/labs/InCode"));
        Directory.CreateDirectory(_root.Folder("code/personal/Personal"));
        var root = _root.Folder("root");
        var code = _root.Folder("code");

        Assert.Equal(_root.Folder("root/Direct"), PackageDashboard.ResolveClone("Direct", null, root, code));
        Assert.Equal(_root.Folder("root/labs/Grouped"), PackageDashboard.ResolveClone("Grouped", "labs/sub", root, code));
        Assert.Equal(_root.Folder("code/labs/InCode"), PackageDashboard.ResolveClone("InCode", "labs", root, code));
        Assert.Equal(_root.Folder("code/personal/Personal"), PackageDashboard.ResolveClone("Personal", null, null, code));
        Assert.Null(PackageDashboard.ResolveClone("Nowhere", null, root, code));
        Assert.Null(PackageDashboard.ResolveClone("Nowhere", "labs", null, code));
    }

    [Fact]
    public void A_row_serialises_with_the_python_field_names()
    {
        var row = new PackageStatusRow("P", "Pkg", "nuget", "m", "1.0.0", "v1.0.0", "1.0.0", true, false, false, false, [], "synced");

        var json = JsonSerializer.Serialize(row);

        Assert.Equal(
            """{"project":"P","package":"Pkg","registry":"nuget","manifest":"m","registry_version":"1.0.0","latest_tag":"v1.0.0","tag_version":"1.0.0","changelog_found":true,"changelog_unreleased":false,"unlisted":false,"deprecated":false,"flags":[],"status":"synced"}""",
            json);
    }

    private static RegistryInfo Published(string version) => new(version) { AllVersions = [version] };

    private async Task<IReadOnlyList<PackageStatusRow>> Collect(string project, string[] tags, Dictionary<string, RegistryInfo> registry, params string[] packages)
    {
        Directory.CreateDirectory(_root.Folder(project));
        _proc.Answer(new Ran(0, string.Join('\n', tags), string.Empty), "git", "tag", "-l");
        var bindings = packages.Select(name => new PackageBinding(project, $"git@github.com:o/{project}.git", new(PackageSpec.NuGet, name, $"{name}.csproj", "release.yml")));
        return await PackageDashboard.CollectAsync(
            bindings, new FakeRegistry(registry), new Git(_proc, _root.Path), _root.Path, _root.Folder("code"), new Dictionary<string, string>(), Token);
    }

    private sealed class FakeRegistry(Dictionary<string, RegistryInfo> packages) : IRegistryInfo
    {
        public Task<RegistryInfo> InfoAsync(string registry, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(packages.GetValueOrDefault(name, RegistryInfo.NotPublished));
    }
}
