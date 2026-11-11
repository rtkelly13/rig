using Rig.Core.Packages;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Packages;

/// <summary><see cref="PackageTracking"/>: local manifest versions against the registry. Ported from <c>tests/test_workspace_packages.py</c>.</summary>
public sealed class PackageTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly PackageSpec Npm = new(PackageSpec.Npm, "@s/pkg", "package.json", "publish.yml");
    private static readonly PackageBinding Binding = new("design-system", "git@github.com:rtkelly-labs/design-system.git", Npm);
    private static readonly PackageBinding Resultful = new(
        "Resultful", "git@github.com:Resultful/Resultful.git", new(PackageSpec.NuGet, "Resultful", "Resultful/Resultful.csproj", "Publish.yml", PackageSpec.Token));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FakePackageFetcher Remote(string version, Release? release) => new()
    {
        Files = { ["package.json"] = PackageAuditTests.Manifest("git+https://github.com/rtkelly-labs/design-system.git", version) },
        Release = release,
    };

    [Fact]
    public async Task A_published_manifest_version_is_synced()
    {
        var record = await PackageTracking.TrackAsync(Binding, Remote("0.12.0", new("0.12.0", Now)), null, Token);

        Assert.Equal((PackageSyncStatus.Synced, "0.12.0", "0.12.0"), (record.Status, record.LocalVersion, record.LatestVersion));
        Assert.Equal("https://www.npmjs.com/package/@s/pkg", record.RegistryUrl);
        Assert.Equal("synced", record.Status.Label());
    }

    [Fact]
    public async Task A_manifest_ahead_of_the_registry_is_unreleased()
    {
        var record = await PackageTracking.TrackAsync(Binding, Remote("0.15.0", new("0.12.0", Now)), null, Token);

        Assert.Equal((PackageSyncStatus.Unreleased, "0.15.0", "0.12.0"), (record.Status, record.LocalVersion, record.LatestVersion));
        Assert.Equal("local ahead (0.15.0 > 0.12.0)", record.Detail);
    }

    [Fact]
    public async Task A_manifest_behind_the_registry_is_behind()
    {
        var record = await PackageTracking.TrackAsync(Binding, Remote("0.11.0", new("0.12.0", Now)), null, Token);

        Assert.Equal(PackageSyncStatus.Behind, record.Status);
        Assert.Contains("local behind", record.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_package_missing_from_the_registry_is_unpublished()
    {
        var record = await PackageTracking.TrackAsync(Binding, Remote("0.1.0", null), null, Token);

        Assert.Equal((PackageSyncStatus.Unpublished, (string?)null), (record.Status, record.LatestVersion));
        Assert.Equal("https://www.nuget.org/packages/Resultful", PackageTracking.RegistryWebUrl(Resultful.Spec));
    }

    [Fact]
    public async Task A_missing_manifest_or_version_is_unknown()
    {
        var fetcher = Remote("0.1.0", new("0.12.0", Now));
        fetcher.Files["package.json"] = """{"name": "@s/pkg"}""";
        Assert.Equal("version not specified in manifest", (await PackageTracking.TrackAsync(Binding, fetcher, null, Token)).Detail);

        fetcher.Files.Clear();
        var record = await PackageTracking.TrackAsync(Binding, fetcher, null, Token);
        Assert.Equal((PackageSyncStatus.Unknown, "local manifest not found"), (record.Status, record.Detail));
    }

    [Fact]
    public async Task The_local_checkout_is_read_before_GitHub()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(dir.File("package.json"), """{"name": "@s/pkg", "version": "0.16.0"}""", Token);

        var record = await PackageTracking.TrackAsync(Binding, Remote("0.12.0", new("0.12.0", Now)), dir.Path, Token);

        Assert.Equal((PackageSyncStatus.Unreleased, "0.16.0"), (record.Status, record.LocalVersion));
    }

    [Fact]
    public async Task A_nuget_project_without_a_version_reads_the_checkout_s_directory_build_props()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Folder("Resultful"));
        await File.WriteAllTextAsync(dir.File("Resultful/Resultful.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""", Token);
        await File.WriteAllTextAsync(dir.File("Directory.Build.props"), "<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix></PropertyGroup></Project>", Token);
        var fetcher = new FakePackageFetcher { Release = new("0.2.0", Now) };

        var record = await PackageTracking.TrackAsync(Resultful, fetcher, dir.Path, Token);

        Assert.Equal((PackageSyncStatus.Unreleased, "1.2.3"), (record.Status, record.LocalVersion));
    }

    [Fact]
    public async Task A_nuget_project_without_any_version_defaults_to_1_0_0()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Folder("Resultful"));
        await File.WriteAllTextAsync(dir.File("Resultful/Resultful.csproj"), """<Project Sdk="Microsoft.NET.Sdk"></Project>""", Token);
        var fetcher = new FakePackageFetcher { Release = new("0.2.0", Now) };

        var record = await PackageTracking.TrackAsync(Resultful, fetcher, dir.Path, Token);

        Assert.Equal((PackageSyncStatus.Unreleased, "1.0.0"), (record.Status, record.LocalVersion));
    }

    [Fact]
    public async Task Latest_and_stable_versions_are_recorded_separately()
    {
        var pre = await PackageTracking.TrackAsync(Binding, Remote("0.2.0-beta05", new("0.2.0-beta05", Now)), null, Token);
        Assert.Equal(("0.2.0-beta05", (string?)null), (pre.LatestVersion, pre.StableVersion));

        var stableAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var mixed = await PackageTracking.TrackAsync(
            Binding, Remote("1.0.0-rc1", new("1.0.0-rc1", Now) { StableVersion = "0.9.0", StablePublished = stableAt }), null, Token);
        Assert.Equal(("1.0.0-rc1", "0.9.0"), (mixed.LatestVersion, mixed.StableVersion));
        Assert.Equal((Now, stableAt), (mixed.LatestPublished!.Value, mixed.StablePublished!.Value));
    }

    [Theory]
    [InlineData(null, "--")]
    [InlineData("2026-10-19T12:00:00Z", "just now")]
    [InlineData("2026-10-19T09:00:00Z", "3h ago")]
    [InlineData("2026-10-16T12:00:00Z", "3d ago")]
    [InlineData("2026-10-05T12:00:00Z", "2w ago")]
    [InlineData("2026-08-19T12:00:00Z", "2mo ago")]
    [InlineData("2024-10-19T12:00:00Z", "2y ago")]
    [InlineData("2026-10-20T12:00:00Z", "in future")]
    public void Ages_read_in_the_largest_whole_unit(string? instant, string age)
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var then = instant is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(age, PackageTracking.RelativeAge(then, now));
    }
}
