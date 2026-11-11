using System.Net;
using Rig.Core.Packages;
using Rig.Core.Tests.Fakes;
using Rig.Core.Tests.Otel;

namespace Rig.Core.Tests.Packages;

/// <summary><see cref="LiveRegistryInfo"/> against recorded registry shapes.</summary>
public sealed class LiveRegistryInfoTests : IDisposable
{
    private const string Flat = LiveRegistryInfo.NuGetFlat + "pkg/index.json";
    private const string Index = LiveRegistryInfo.NuGetRegistrations + "pkg/index.json";
    private const string Page = LiveRegistryInfo.NuGetRegistrations + "pkg/page.json";
    private const string Packument = LiveRegistryInfo.NpmRegistry + "@s%2fpkg";

    private readonly FakeHttp _http = new();
    private readonly HttpClient _client;

    public LiveRegistryInfoTests() => _client = _http.Client();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task A_missing_package_is_unpublished_and_any_other_failure_is_a_failed_lookup(HttpStatusCode status, bool failed)
    {
        using var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new HttpClient(handler);
        var live = new LiveRegistryInfo(client);

        foreach (var registry in new[] { PackageSpec.NuGet, PackageSpec.Npm })
        {
            var info = await live.InfoAsync(registry, "Some.Package", Token);
            Assert.Equal(((string?)null, failed), (info.LatestVersion, info.Failed));
        }
    }

    [Fact]
    public async Task A_network_error_is_a_failed_lookup()
    {
        using var handler = new StubHandler(_ => throw new HttpRequestException("offline"));
        using var client = new HttpClient(handler);

        Assert.True((await new LiveRegistryInfo(client).InfoAsync(PackageSpec.Npm, "x", Token)).Failed);
    }

    [Fact]
    public async Task An_unknown_registry_is_unpublished() =>
        Assert.Same(RegistryInfo.NotPublished, await new LiveRegistryInfo(_client).InfoAsync("pypi", "x", Token));

    [Fact]
    public async Task NuGet_reads_the_newest_version_and_whether_it_is_listed_or_deprecated()
    {
        _http.Routes[Flat] = """{"versions": ["0.9.0", "1.0.0", "0.10.0"]}""";
        _http.Routes[Index] = $$$"""{"items": [{"@id": "{{{Page}}}"}]}""";
        _http.Routes[Page] = """
            {"items": [
              {"catalogEntry": {"version": "1.0.0", "listed": false, "deprecation": {"reasons": ["Legacy"]}}},
              {"catalogEntry": {"version": "0.10.0", "listed": true}}]}
            """;
        var live = new LiveRegistryInfo(_client);

        var info = await live.InfoAsync(PackageSpec.NuGet, "Pkg", Token);
        await live.InfoAsync(PackageSpec.NuGet, "PKG", Token);

        Assert.Equal("1.0.0", info.LatestVersion);
        Assert.Equal(["0.9.0", "1.0.0", "0.10.0"], info.AllVersions);
        Assert.True(info.Unlisted && info.Deprecated);
        Assert.Single(_http.Requested, url => url == Flat);
    }

    [Fact]
    public async Task NuGet_without_a_registration_index_is_listed_and_not_deprecated()
    {
        _http.Routes[Flat] = """{"versions": ["1.0.0"]}""";

        var info = await new LiveRegistryInfo(_client).InfoAsync(PackageSpec.NuGet, "Pkg", Token);

        Assert.Equal(("1.0.0", false, false), (info.LatestVersion, info.Unlisted, info.Deprecated));
    }

    [Fact]
    public async Task NuGet_with_no_versions_is_unpublished()
    {
        _http.Routes[Flat] = """{"versions": []}""";

        Assert.Same(RegistryInfo.NotPublished, await new LiveRegistryInfo(_client).InfoAsync(PackageSpec.NuGet, "Pkg", Token));
    }

    [Fact]
    public async Task Npm_takes_the_latest_dist_tag_and_its_deprecation()
    {
        _http.Routes[Packument] = """
            {"dist-tags": {"latest": "1.0.0"},
             "versions": {"1.0.0": {"deprecated": "use @s/other"}, "2.0.0-beta.1": {}}}
            """;

        var info = await new LiveRegistryInfo(_client).InfoAsync(PackageSpec.Npm, "@s/pkg", Token);

        Assert.Equal(("1.0.0", true), (info.LatestVersion, info.Deprecated));
        Assert.Equal(["1.0.0", "2.0.0-beta.1"], info.AllVersions);
    }

    [Fact]
    public async Task Npm_without_a_usable_dist_tag_takes_the_newest_version()
    {
        _http.Routes[Packument] = """{"dist-tags": {"latest": "9.9.9"}, "versions": {"0.2.0": {}, "0.10.0": {}}}""";

        var info = await new LiveRegistryInfo(_client).InfoAsync(PackageSpec.Npm, "@s/pkg", Token);

        Assert.Equal(("0.10.0", false), (info.LatestVersion, info.Deprecated));
    }

    [Fact]
    public async Task Registry_documents_of_an_unexpected_shape_are_read_leniently()
    {
        _http.Routes[Flat] = "[]";
        _http.Routes[Packument] = """{"dist-tags": "latest", "versions": {"1.0.0": "odd"} }""";
        var live = new LiveRegistryInfo(_client);

        Assert.Same(RegistryInfo.NotPublished, await live.InfoAsync(PackageSpec.NuGet, "Pkg", Token));
        var npm = await live.InfoAsync(PackageSpec.Npm, "@s/pkg", Token);
        Assert.Equal(("1.0.0", false), (npm.LatestVersion, npm.Deprecated));
    }

    [Fact]
    public async Task Npm_with_no_versions_is_unpublished()
    {
        _http.Routes[Packument] = """{"versions": {}}""";

        Assert.Same(RegistryInfo.NotPublished, await new LiveRegistryInfo(_client).InfoAsync(PackageSpec.Npm, "@s/pkg", Token));
    }
}
