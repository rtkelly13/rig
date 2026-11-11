using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Packages;
using Rig.Core.Processes;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Packages;

/// <summary>
/// <see cref="LivePackageFetcher"/> against recorded registry shapes. Ported from the live-fetcher tests in
/// <c>tests/test_workspace_packages.py</c> and <c>tests/test_workspace_nuget_governance.py</c>.
/// </summary>
public sealed class LivePackageFetcherTests : IDisposable
{
    private const string Packument = "https://registry.npmjs.org/@s%2fpkg";
    private const string Attestations = "https://registry.npmjs.org/-/npm/v1/attestations/@s%2fpkg@0.12.0";
    private const string Registrations = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private const string Flat = "https://api.nuget.org/v3-flatcontainer/";
    private const string Search = "https://azuresearch-usnc.nuget.org/query";
    private const string Leaf = "https://api.nuget.org/v3/catalog0/data/x/p.json";
    private const string SourceGenerator = "https://github.com/rtkelly13/Parquet.SourceGenerator.git";

    private static readonly PackageSpec Npm = new(PackageSpec.Npm, "@s/pkg", "package.json", "publish.yml");
    private static readonly PackageSpec Pkg = new(PackageSpec.NuGet, "Pkg", "Pkg/Pkg.csproj", "release.yml");
    private static readonly PackageSpec Parquet = new(PackageSpec.NuGet, "Parquet.SourceGenerator", "src/P.csproj", "release.yml");

    private readonly FakeHttp _http = new();
    private readonly FakeGitHub _github = new();
    private readonly FakeProc _proc = new();
    private readonly HttpClient _client;

    public LivePackageFetcherTests() => _client = _http.Client();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private LivePackageFetcher Fetcher => new(_client, _github, _proc);

    public void Dispose()
    {
        _client.Dispose();
        _http.Dispose();
    }

    [Fact]
    public async Task An_npm_packument_and_its_provenance_are_decoded()
    {
        NpmRoutes();

        var release = await Fetcher.RegistryReleaseAsync(Npm, Token);

        Assert.NotNull(release);
        Assert.Equal("0.12.0", release.Version);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 44, 2, 600, TimeSpan.Zero), release.Published);
        Assert.True(release.TrustedPublisher);
        Assert.Equal(new Provenance("https://github.com/rtkelly13/design-system", "p.yml", 1313191156, 11708460), release.Provenance);
    }

    [Fact]
    public async Task Odd_optional_npm_fields_lose_only_their_metadata()
    {
        _http.Routes[Packument] = """
            {"dist-tags": {"latest": "1.0.0"}, "time": [],
             "versions": {"1.0.0": {"_npmUser": "someone", "dist": {"attestations": "unexpected"} } } }
            """;

        var release = await Fetcher.RegistryReleaseAsync(Npm, Token);

        Assert.NotNull(release);
        Assert.Equal(("1.0.0", (bool?)null, (Provenance?)null), (release.Version, release.TrustedPublisher, release.Provenance));
    }

    [Fact]
    public async Task A_release_is_fetched_once_per_spec()
    {
        NpmRoutes();
        var fetcher = Fetcher;

        await fetcher.RegistryReleaseAsync(Npm, Token);
        await fetcher.RegistryReleaseAsync(Npm, Token);

        Assert.Single(_http.Requested, url => url == Packument);
    }

    [Fact]
    public async Task A_missing_npm_package_is_null() =>
        Assert.Null(await Fetcher.RegistryReleaseAsync(Npm, Token));

    [Fact]
    public async Task An_npm_prerelease_is_latest_and_the_newest_stable_is_kept_beside_it()
    {
        _http.Routes[Packument] = """
            {"dist-tags": {"latest": "0.9.0"},
             "versions": {"0.9.0": {"_npmUser": {"name": "u"}}, "1.0.0-beta.1": {"_npmUser": {"name": "u"}}},
             "time": {"0.9.0": "2025-01-01T00:00:00.000Z", "1.0.0-beta.1": "2026-10-01T00:00:00.000Z"}}
            """;

        var release = await Fetcher.RegistryReleaseAsync(Npm, Token);

        Assert.Equal(("1.0.0-beta.1", "0.9.0"), (release!.Version, release.StableVersion));
        Assert.False(release.TrustedPublisher);
        Assert.Null(release.Provenance);
    }

    [Fact]
    public async Task NuGet_registration_pages_are_followed_to_the_catalog()
    {
        const string Base = Registrations + "pkg";
        NuGetRoutes("Pkg", ["0.0.3", "0.0.4"]);
        _http.Routes[$"{Base}/index.json"] = $$$"""{"items": [{"@id": "{{{Base}}}/page.json"}]}""";
        _http.Routes[$"{Base}/page.json"] = $$$"""
            {"@id": "{{{Base}}}/page.json", "items": [
              {"catalogEntry": {"@id": "{{{Leaf}}}", "version": "0.0.3", "listed": true, "published": "2026-09-05T21:11:52.6Z"}},
              {"catalogEntry": {"@id": "u", "version": "0.0.4", "listed": false}}]}
            """;
        _http.Routes[Leaf] = $$$"""{"repository": {"type": "git", "url": "{{{SourceGenerator}}}"}}""";

        var release = await Fetcher.RegistryReleaseAsync(Pkg, Token);

        Assert.Equal(("0.0.3", SourceGenerator), (release!.Version, release.SourceRepoUrl));
        Assert.True(release.Listed);
        Assert.Null(release.Owners);
    }

    [Fact]
    public async Task A_nuget_prerelease_is_latest_and_the_newest_stable_is_kept_beside_it()
    {
        const string Base = Registrations + "pkg";
        NuGetRoutes("Pkg", ["0.9.0", "1.0.0-rc1", "1.0.0-rc2"]);
        _http.Routes[$"{Base}/index.json"] = $$$"""
            {"items": [{"@id": "{{{Base}}}/page.json", "items": [
              {"catalogEntry": {"@id": "{{{Base}}}/0.9.0.json", "version": "0.9.0", "listed": true, "published": "2025-01-01T00:00:00Z"}},
              {"catalogEntry": {"@id": "{{{Base}}}/1.0.0-rc1.json", "version": "1.0.0-rc1", "listed": true, "published": "2026-09-01T00:00:00Z"}},
              {"catalogEntry": {"@id": "{{{Base}}}/1.0.0-rc2.json", "version": "1.0.0-rc2", "listed": true, "published": "2026-10-01T00:00:00Z"}}]}]}
            """;

        var release = await Fetcher.RegistryReleaseAsync(Pkg, Token);

        Assert.Equal(("1.0.0-rc2", "0.9.0"), (release!.Version, release.StableVersion));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), release.Published);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), release.StablePublished);
    }

    [Fact]
    public async Task Owners_deprecation_and_vulnerabilities_are_read()
    {
        ParquetRoutes(
            """
            {"@id": "https://api.nuget.org/v3/catalog0/data/x/p.json", "version": "0.0.3", "listed": true,
             "deprecation": {"reasons": ["Legacy"], "message": "moved", "alternatePackage": {"id": "New.Pkg"}},
             "vulnerabilities": [{"advisoryUrl": "https://github.com/advisories/GHSA-x", "severity": "2"}, {"severity": "9"}]}
            """,
            owners: true);
        _http.Routes[Leaf] = $$$"""{"repository": {"type": "git", "url": "{{{SourceGenerator}}}"}}""";

        var release = await Fetcher.RegistryReleaseAsync(Parquet, Token);

        Assert.Equal(["rtk_13"], release!.Owners!);
        Assert.Equal("Legacy (moved); use New.Pkg", release.Deprecation);
        Assert.Equal(["high https://github.com/advisories/GHSA-x", "unknown"], release.Vulnerabilities);
        Assert.Equal(SourceGenerator, release.SourceRepoUrl);
    }

    [Fact]
    public async Task A_deprecation_without_reasons_says_so() =>
        Assert.Equal("no reason given", await DeprecationOf("""{"reasons": []}"""));

    [Fact]
    public async Task An_all_unlisted_package_still_reports_its_newest_version()
    {
        ParquetRoutes("""{"@id": "https://api.nuget.org/v3/catalog0/data/x/p.json", "version": "0.0.3", "listed": false}""", owners: false);

        var release = await Fetcher.RegistryReleaseAsync(Parquet, Token);

        Assert.Equal(("0.0.3", false, (IReadOnlyList<string>?)null), (release!.Version, release.Listed, release.Owners));
    }

    [Fact]
    public async Task A_flat_container_404_means_no_package()
    {
        NuGetRoutes("Other", ["1.0.0"], owners: ["rtk_13"]);

        Assert.Null(await Fetcher.RegistryReleaseAsync(Parquet, Token));
    }

    [Fact]
    public async Task Without_a_service_index_nothing_is_read()
    {
        Assert.Null(await Fetcher.RegistryReleaseAsync(Parquet, Token));
        Assert.Equal([LivePackageFetcher.NuGetServiceIndex], _http.Requested);
    }

    [Fact]
    public async Task A_github_repo_is_its_name_and_ids()
    {
        _github.On("GET", "repos/o/r", """{"full_name": "O/R", "id": 7, "owner": {"id": 9}}""");

        Assert.Equal(new RepoIdentity("O/R", 7, 9), await Fetcher.GitHubRepoAsync("o/r", Token));
        Assert.Null(await Fetcher.GitHubRepoAsync("o/missing", Token));
    }

    [Fact]
    public async Task A_github_file_is_read_raw()
    {
        _github.On("GET", "repos/o/r/contents/package.json", """{"version": "1.0.0"}""");

        Assert.Equal("""{"version": "1.0.0"}""", await Fetcher.GitHubFileAsync("o/r", "package.json", Token));
        Assert.Equal(["Accept: application/vnd.github.raw"], Assert.Single(_github.Calls).Headers);
        Assert.Null(await Fetcher.GitHubFileAsync("o/r", "missing.json", Token));
    }

    [Fact]
    public async Task Workflow_conclusions_skip_runs_without_one()
    {
        _github.On(
            "GET",
            "repos/o/r/actions/workflows/p.yml/runs?status=completed&per_page=5",
            """{"workflow_runs": [{"conclusion": "success"}, {"conclusion": null}, {"conclusion": ""}, {"conclusion": "failure"}]}""");

        Assert.Equal(["success", "failure"], (await Fetcher.WorkflowConclusionsAsync("o/r", "p.yml", 5, Token))!);
        Assert.Null(await Fetcher.WorkflowConclusionsAsync("o/r", "other.yml", 5, Token));
    }

    [Fact]
    public async Task Npm_trust_reads_the_listing_with_the_console_attached_for_a_one_time_password()
    {
        _proc.Answer(new Ran(0, """[{"id":"x","type":"github","claims":{"repository":"o/r","workflow_ref":{"file":"p.yml"}}}]""", string.Empty));

        var listing = await Fetcher.NpmTrustAsync("@s/pkg", Token);

        Assert.Equal([new TrustConfig("o/r", "p.yml")], listing.Configs);
        Assert.Null(listing.Error);
        var request = Assert.Single(_proc.Requests);
        Assert.True(request.ConsoleInput);
        Assert.Equal(["trust", "list", "@s/pkg", "--json"], request.Argv.Skip(1));
    }

    [Fact]
    public async Task An_npm_trust_failure_is_reported_not_thrown()
    {
        _proc.Answer(new Ran(1, string.Empty, string.Empty));

        var listing = await Fetcher.NpmTrustAsync("@s/pkg", Token);

        Assert.Empty(listing.Configs);
        Assert.Contains("npm >= 11.15", listing.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"claims":{"repository":"o/r","workflow_ref":{"file":"p.yml"},"environment":"npm"}}""", "o/r", "npm")]
    [InlineData("""[{"claims":{"repository":"o/r","workflow_ref":{"file":"p.yml"}}}, {"id":"no-claims"}]""", "o/r", null)]
    public void Npm_trust_is_one_entry_or_a_list(string json, string repository, string? environment) =>
        Assert.Equal([new TrustConfig(repository, "p.yml", environment)], LivePackageFetcher.ParseNpmTrust(json));

    [Theory]
    [InlineData("not json")]
    [InlineData("42")]
    public void Unreadable_npm_trust_is_empty(string text) => Assert.Empty(LivePackageFetcher.ParseNpmTrust(text));

    [Fact]
    public void Provenance_skips_attestations_that_are_not_slsa()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Statement("1")));
        var document = JsonNode.Parse($$$"""{"attestations": [{"predicateType": "https://example.com/other", "bundle": {"dsseEnvelope": {"payload": "{{{payload}}}"} } }]}""");

        Assert.Null(LivePackageFetcher.ParseNpmProvenance(document));
        Assert.Null(LivePackageFetcher.ParseNpmProvenance(null));
    }

    [Fact]
    public void Provenance_without_a_workflow_is_null()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"predicate": {"buildDefinition": {}}}"""));
        var document = JsonNode.Parse($$$"""{"attestations": [{"predicateType": "https://slsa.dev/provenance/v1", "bundle": {"dsseEnvelope": {"payload": "{{{payload}}}"} } }]}""");

        Assert.Null(LivePackageFetcher.ParseNpmProvenance(document));
    }

    private static string Statement(string ownerId) => $$$"""
        {"predicate": {"buildDefinition": {
          "externalParameters": {"workflow": {"repository": "https://github.com/rtkelly13/design-system", "path": "p.yml"}},
          "internalParameters": {"github": {"repository_id": "1313191156", "repository_owner_id": "{{{ownerId}}}"} } } } }
        """;

    private async Task<string?> DeprecationOf(string deprecation)
    {
        ParquetRoutes($$$"""{"@id": "{{{Leaf}}}", "version": "0.0.3", "listed": true, "deprecation": {{{deprecation}}}}""", owners: false);
        return (await Fetcher.RegistryReleaseAsync(Parquet, Token))!.Deprecation;
    }

    private void NpmRoutes()
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(Statement("11708460")));
        _http.Routes[Packument] = $$$"""
            {"dist-tags": {"latest": "0.12.0"},
             "time": {"created": "2026-07-01T00:00:00Z", "0.12.0": "2026-09-25T12:44:02.600Z"},
             "versions": {"0.12.0": {
               "_npmUser": {"name": "GitHub Actions", "trustedPublisher": {"id": "github", "oidcConfigId": "x"}},
               "dist": {"attestations": {"url": "{{{Attestations}}}", "provenance": {} } } } } }
            """;
        _http.Routes[Attestations] = $$$"""
            {"attestations": [{"predicateType": "https://slsa.dev/provenance/v1", "bundle": {"dsseEnvelope": {"payload": "{{{payload}}}"} } }]}
            """;
    }

    // The service index lists a semver1 decoy and two search hosts, as nuget.org does.
    private void NuGetRoutes(string name, string[] versions, string[]? owners = null)
    {
        _http.Routes[LivePackageFetcher.NuGetServiceIndex] = $$$"""
            {"version": "3.0.0", "resources": [
              {"@id": "https://api.nuget.org/v3/registration5-semver1/", "@type": "RegistrationsBaseUrl"},
              {"@id": "{{{Registrations}}}", "@type": "RegistrationsBaseUrl/3.6.0"},
              {"@id": "{{{Flat}}}", "@type": "PackageBaseAddress/3.0.0"},
              {"@id": "{{{Search}}}", "@type": "SearchQueryService/3.5.0"},
              {"@id": "https://azuresearch-ussc.nuget.org/query", "@type": "SearchQueryService/3.5.0"}]}
            """;
        _http.Routes[$"{Flat}{name.ToLowerInvariant()}/index.json"] = new JsonObject { ["versions"] = new JsonArray([.. versions.Select(v => JsonValue.Create(v))]) }.ToJsonString();
        if (owners is not null)
        {
            _http.Routes[$"{Search}?q=packageid:{name}&prerelease=true&semVerLevel=2.0.0"] = new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject { ["id"] = name, ["owners"] = new JsonArray([.. owners.Select(o => JsonValue.Create(o))]) }),
            }.ToJsonString();
        }
    }

    private void ParquetRoutes(string entry, bool owners)
    {
        const string Base = Registrations + "parquet.sourcegenerator";
        NuGetRoutes("Parquet.SourceGenerator", ["0.0.1", "0.0.3"], owners ? ["rtk_13"] : null);
        _http.Routes[$"{Base}/index.json"] = $$$"""{"items": [{"@id": "{{{Base}}}/page.json", "items": [{"catalogEntry": {{{entry}}}}]}]}""";
    }
}
