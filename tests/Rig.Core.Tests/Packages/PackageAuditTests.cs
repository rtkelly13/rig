using System.Text.Json;
using Rig.Core.Packages;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Packages;

/// <summary>
/// <see cref="PackageAudit"/>, <see cref="PackageVersions"/>, <see cref="ManifestFields"/> and
/// <see cref="PackageBindings"/>: the npm and repo-level checks. Ported from <c>tests/test_workspace_packages.py</c>.
/// </summary>
public sealed class PackageAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    private static readonly RepoIdentity Labs = new("rtkelly-labs/design-system", 1313191156, 330611343);
    private const string OidcWorkflow = "permissions:\n  contents: read\n  id-token: write\n";
    private const string LabsRemote = "git@github.com:rtkelly-labs/design-system.git";
    private static readonly PackageSpec Npm = new(PackageSpec.Npm, "@s/pkg", "package.json", "publish.yml");
    private static readonly PackageSpec NuGet = new(PackageSpec.NuGet, "Pkg", "Directory.Build.props", "release.yml");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static string Manifest(string url, string version = "0.12.0") =>
        JsonSerializer.Serialize(new { name = "@s/pkg", version, repository = new { type = "git", url } });

    private static Release LabsRelease(long ownerId = 330611343, string repo = "rtkelly-labs/design-system") =>
        new("0.12.0", new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero))
        {
            TrustedPublisher = true,
            Provenance = new($"https://github.com/{repo}", ".github/workflows/publish.yml", Labs.Id, ownerId),
        };

    private static FakePackageFetcher NpmFetcher() => new()
    {
        Repo = Labs,
        Files =
        {
            ["package.json"] = Manifest("git+https://github.com/rtkelly-labs/design-system.git"),
            [".github/workflows/publish.yml"] = OidcWorkflow,
        },
        Release = LabsRelease(),
    };

    private static FakePackageFetcher NuGetFetcher(string? source) => new()
    {
        Repo = Labs,
        Files =
        {
            ["Directory.Build.props"] = "<RepositoryUrl>https://github.com/rtkelly-labs/design-system.git</RepositoryUrl>",
            [".github/workflows/release.yml"] = OidcWorkflow,
        },
        Release = new("0.0.3", null) { SourceRepoUrl = source },
    };

    private static async Task<Dictionary<string, Finding>> Audit(PackageSpec spec, FakePackageFetcher fetcher, string repo = LabsRemote, bool trust = false)
    {
        var findings = await PackageAudit.AuditAsync([new("design-system", repo, spec)], fetcher, Now, trust, Token);
        var byCheck = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            byCheck[finding.Check] = finding;
        }

        return byCheck;
    }

    [Fact]
    public void An_npm_manifest_s_repository_is_a_string_or_an_object_s_url()
    {
        Assert.Equal(new ManifestFields("https://github.com/o/r.git", "1.2.3"), ManifestFields.Read(PackageSpec.Npm, Manifest("https://github.com/o/r.git", "1.2.3")));
        Assert.Equal(new ManifestFields("github:o/r", null), ManifestFields.Read(PackageSpec.Npm, """{"repository": "github:o/r"}"""));
        Assert.Equal(new ManifestFields(null, null), ManifestFields.Read(PackageSpec.Npm, "not json"));
    }

    [Fact]
    public void An_msbuild_manifest_gives_its_repository_url_and_version()
    {
        const string props = "<Project><PropertyGroup><RepositoryUrl>https://github.com/o/r.git</RepositoryUrl></PropertyGroup></Project>";
        Assert.Equal(new ManifestFields("https://github.com/o/r.git", null), ManifestFields.Read(PackageSpec.NuGet, props));
        Assert.Equal(new ManifestFields(null, "0.2.0"), ManifestFields.Read(PackageSpec.NuGet, "<VersionPrefix>0.2.0</VersionPrefix>"));
    }

    [Fact]
    public void Prereleases_order_before_their_release()
    {
        Assert.True(PackageVersions.IsAhead("0.16.0", "0.12.0"));
        Assert.True(PackageVersions.IsAhead("0.12.0", "0.12.0-beta1"));
        Assert.False(PackageVersions.IsAhead("0.12.0", "0.12.0"));
        Assert.False(PackageVersions.IsAhead("0.9.0", "0.12.0"));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("0.0.3", true)]
    [InlineData("1.0.0+meta123", true)]
    [InlineData("0.2.0-beta05", false)]
    [InlineData("1.0.0-rc1", false)]
    [InlineData("16.5.0-canary.18", false)]
    public void A_version_without_a_prerelease_is_stable(string version, bool stable) => Assert.Equal(stable, PackageVersions.IsStable(version));

    [Theory]
    [InlineData("0.2.0-beta05", "0.2.0-beta06")]
    [InlineData("16.5.0-canary.2", "16.5.0-canary.18")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-rc1")]
    [InlineData("1.0.0-rc1", "1.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0-1", "1.0.0-a")]
    public void Versions_order_part_by_part(string lower, string higher)
    {
        Assert.True(PackageVersions.Compare(lower, higher) < 0);
        Assert.True(PackageVersions.Compare(higher, lower) > 0);
    }

    [Fact]
    public void A_stable_release_is_its_own_stable_version()
    {
        var stable = new Release("0.0.3", Now);
        Assert.Equal(("0.0.3", (DateTimeOffset?)Now), (stable.StableVersion, stable.StablePublished));

        var pre = new Release("0.2.0-beta05", Now);
        Assert.Equal((null, null), (pre.StableVersion, pre.StablePublished));

        var earlier = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var custom = new Release("1.0.0-rc1", Now) { StableVersion = "0.9.0", StablePublished = earlier };
        Assert.Equal(("0.9.0", (DateTimeOffset?)earlier), (custom.StableVersion, custom.StablePublished));
    }

    [Fact]
    public void Failures_count_back_to_the_last_success_past_cancelled_runs()
    {
        Assert.Equal(2, PackageAudit.ConsecutiveFailures(["failure", "cancelled", "failure", "success", "failure"]));
        Assert.Equal(0, PackageAudit.ConsecutiveFailures(["cancelled", "success"]));
        Assert.Equal(2, PackageAudit.ConsecutiveFailures(["timed_out", "skipped", "startup_failure"]));
    }

    [Fact]
    public void Bindings_come_from_enabled_projects_only()
    {
        using var dir = new TempDir();
        var config = dir.File("managed-projects.json");
        const string package = """{"registry": "npm", "name": "@s/p", "manifest": "package.json", "workflow": "p.yml"}""";
        File.WriteAllText(config, $$"""
            {"projects": [
              {"name": "a", "repo": "git@github.com:o/a.git", "packages": [{{package}}]},
              {"name": "b", "repo": "git@github.com:o/b.git", "enabled": false, "packages": [{{package}}]},
              {"name": "c", "repo": "git@github.com:o/c.git"}
            ]}
            """);

        var binding = Assert.Single(PackageBindings.Load(config));
        Assert.Equal(("a", "@s/p", PackageSpec.Oidc), (binding.Project, binding.Spec.Name, binding.Spec.Auth));
    }

    [Fact]
    public void Policy_fields_decode_and_default_to_unset()
    {
        using var dir = new TempDir();
        var config = dir.File("managed-projects.json");
        File.WriteAllText(config, """
            {"projects": [{"name": "a", "repo": "git@github.com:o/a.git", "packages": [
              {"registry": "nuget", "name": "P", "manifest": "m", "workflow": "release.yml", "nuget_owner": "rtkelly-labs",
               "nuget_user": "rtk_13", "owners": ["rtk_13"], "scopes": ["push-new", "push-version"]},
              {"registry": "nuget", "name": "Q", "manifest": "m", "workflow": "ci.yml", "auth": "token"}
            ]}]}
            """);

        var bindings = PackageBindings.Load(config);
        var (first, second) = (bindings[0].Spec, bindings[1].Spec);
        Assert.Equal(("rtkelly-labs", "rtk_13", (string?)null), (first.NuGetOwner, first.NuGetUser, first.Environment));
        Assert.Equal(["rtk_13"], first.Owners);
        Assert.Equal(["push-new", "push-version"], first.Scopes);
        Assert.Equal(((string?)null, (string?)null, 0, 0), (second.NuGetOwner, second.NuGetUser, second.Owners.Count, second.Scopes.Count));
        Assert.Equal(PackageSpec.Token, second.Auth);
    }

    [Theory]
    [InlineData("""{"projects": [{"name": "a", "repo": "r", "packages": [{"registry": "pypi", "name": "p", "manifest": "m", "workflow": "w"}]}]}""", "pypi")]
    [InlineData("""{"projects": [{"name": "a", "repo": "r", "packages": [{"registry": "npm", "name": "p", "manifest": "m", "workflow": "w", "auth": "key"}]}]}""", "auth")]
    [InlineData("""{"projects": [{"name": "a", "repo": "r", "packages": [{"registry": "npm", "name": "p", "manifest": "m"}]}]}""", "workflow: required")]
    [InlineData("""{"projects": [{"name": "a", "repo": "r", "enabled": "yes"}]}""", "enabled")]
    [InlineData("""{"projects": [{"name": "a", "repo": "r", "packages": [{"registry": "npm", "name": "p", "manifest": "m", "workflow": "w", "owners": [1]}]}]}""", "owners")]
    [InlineData("""{"projects": {"name": "a"}}""", "projects: expected a list")]
    [InlineData("""{"projects": [3]}""", "expected an object")]
    [InlineData("""[]""", "expected an object")]
    [InlineData("""{"projects": [""", "managed-projects.json")]
    public void A_malformed_binding_is_rejected_by_path(string json, string message)
    {
        using var dir = new TempDir();
        var config = dir.File("managed-projects.json");
        File.WriteAllText(config, json);

        var error = Assert.Throws<InvalidDataException>(() => PackageBindings.Load(config));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_repo_config_declares_valid_bindings_and_the_parquet_policy()
    {
        var config = Path.Combine(Path.GetDirectoryName(RepoTemp.Root)!, "config", "managed-projects.json");
        var bindings = PackageBindings.Load(config);
        Assert.NotEmpty(bindings);

        var parquet = bindings.Where(b => b.Project == "Parquet.SourceGenerator").Select(b => b.Spec).ToList();
        Assert.Equal(3, parquet.Count);
        Assert.All(parquet, spec =>
        {
            Assert.Equal(("rtkelly-labs", "rtk_13", (string?)null), (spec.NuGetOwner, spec.NuGetUser, spec.Environment));
            Assert.Equal(["push-new", "push-version"], spec.Scopes);
        });
    }

    [Fact]
    public async Task A_healthy_npm_package_is_all_ok()
    {
        var found = await Audit(Npm, NpmFetcher());

        Assert.Equal(["repo", "manifest", "version", "workflow", "trusted-publisher", "provenance", "runs"], found.Keys);
        Assert.All(found.Values, f => Assert.Equal(CheckStatus.Ok, f.Status));
        Assert.Equal("0.12.0 built from rtkelly-labs/design-system/.github/workflows/publish.yml", found["provenance"].Detail);
    }

    [Fact]
    public async Task A_transfer_shows_in_the_provenance_owner_id()
    {
        var fetcher = NpmFetcher();
        fetcher.Release = LabsRelease(ownerId: 11708460, repo: "rtkelly13/design-system");

        var finding = (await Audit(Npm, fetcher))["provenance"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("repository moved", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("11708460 -> 330611343", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("npm trust github @s/pkg --repo rtkelly-labs/design-system --file publish.yml", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_different_repository_id_is_not_reported_as_a_move()
    {
        var fetcher = NpmFetcher();
        fetcher.Release = new("0.12.0", null) { Provenance = new("https://github.com/x/fork", "p.yml", 42, Labs.OwnerId) };

        var finding = (await Audit(Npm, fetcher))["provenance"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("different repository (x/fork, id 42)", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provenance_from_another_name_with_the_same_ids_fails_with_the_trust_fix()
    {
        var fetcher = NpmFetcher();
        fetcher.Release = new("0.12.0", null) { Provenance = new("https://github.com/other/name", "p.yml", null, null) };
        var spec = Npm with { Environment = "npm" };

        var finding = (await Audit(spec, fetcher))["provenance"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Equal("0.12.0 built from other/name", finding.Detail);
        Assert.EndsWith("--allow-publish --env npm", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_manifest_url_fails_for_npm()
    {
        var fetcher = NpmFetcher();
        fetcher.Files["package.json"] = Manifest("git+https://github.com/rtkelly13/design-system.git");

        var finding = (await Audit(Npm, fetcher))["manifest"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("422", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Npm_compares_the_manifest_slug_case_sensitively()
    {
        var fetcher = NpmFetcher();
        fetcher.Files["package.json"] = Manifest("https://github.com/RTKelly-Labs/design-system");

        Assert.Equal(CheckStatus.Fail, (await Audit(Npm, fetcher))["manifest"].Status);
    }

    [Fact]
    public async Task A_manifest_without_a_url_warns_and_a_missing_one_fails()
    {
        var fetcher = NpmFetcher();
        fetcher.Files["package.json"] = """{"version": "0.12.0"}""";
        Assert.Equal(CheckStatus.Warn, (await Audit(Npm, fetcher))["manifest"].Status);

        fetcher.Files.Remove("package.json");
        var found = await Audit(Npm, fetcher);
        Assert.Equal(CheckStatus.Fail, found["manifest"].Status);
        Assert.False(found.ContainsKey("version"));
    }

    [Fact]
    public async Task An_unpublished_version_warns_with_its_age()
    {
        var fetcher = NpmFetcher();
        fetcher.Files["package.json"] = Manifest("https://github.com/rtkelly-labs/design-system", "0.16.0");

        var finding = (await Audit(Npm, fetcher))["version"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Equal("manifest is at 0.16.0; the registry's latest is 0.12.0, published 21 days ago", finding.Detail);
    }

    [Fact]
    public async Task A_stale_declared_repo_fails()
    {
        var found = await Audit(Npm, NpmFetcher(), repo: "git@github.com:rtkelly13/design-system.git");

        Assert.Equal(CheckStatus.Fail, found["repo"].Status);
        Assert.Contains("redirects to rtkelly-labs/design-system", found["repo"].Detail, StringComparison.Ordinal);
        Assert.Equal("set design-system's repo to git@github.com:rtkelly-labs/design-system.git", found["repo"].Fix);
    }

    [Fact]
    public async Task An_unresolvable_repo_stops_the_audit()
    {
        var fetcher = NpmFetcher();
        fetcher.Repo = null;

        var found = await Audit(Npm, fetcher);

        Assert.Equal(["repo"], found.Keys);
        Assert.Equal(CheckStatus.Fail, found["repo"].Status);
        Assert.Equal(CheckStatus.Fail, (await Audit(Npm, NpmFetcher(), repo: "not a remote"))["repo"].Status);
    }

    [Fact]
    public async Task An_oidc_workflow_without_id_token_fails()
    {
        var fetcher = NpmFetcher();
        fetcher.Files[".github/workflows/publish.yml"] = "permissions:\n  contents: read\n";
        Assert.Equal(CheckStatus.Fail, (await Audit(Npm, fetcher))["workflow"].Status);

        fetcher.Files.Remove(".github/workflows/publish.yml");
        Assert.Equal(".github/workflows/publish.yml not found on the default branch", (await Audit(Npm, fetcher))["workflow"].Detail);
    }

    [Fact]
    public async Task An_oidc_workflow_still_referencing_a_token_warns()
    {
        var fetcher = NpmFetcher();
        fetcher.Files[".github/workflows/publish.yml"] = OidcWorkflow + "NODE_AUTH_TOKEN: ${{ secrets.NPM_TOKEN }}\n";

        var finding = (await Audit(Npm, fetcher))["workflow"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Equal("OIDC enabled but NPM_TOKEN still referenced", finding.Detail);
    }

    [Fact]
    public async Task Token_auth_warns_to_migrate()
    {
        var finding = (await Audit(Npm with { Auth = PackageSpec.Token }, NpmFetcher()))["workflow"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Equal("publishes with a long-lived token (secret)", finding.Detail);
    }

    [Fact]
    public async Task A_token_published_latest_warns_on_an_oidc_package()
    {
        var fetcher = NpmFetcher();
        fetcher.Release = new("0.12.0", null) { TrustedPublisher = false };

        var found = await Audit(Npm, fetcher);

        Assert.Equal(CheckStatus.Warn, found["trusted-publisher"].Status);
        Assert.Equal(CheckStatus.Warn, found["provenance"].Status);
        Assert.Equal(CheckStatus.Skip, (await Audit(Npm with { Auth = PackageSpec.Token }, fetcher))["provenance"].Status);
    }

    [Fact]
    public async Task A_missing_package_points_at_the_bootstrap()
    {
        var fetcher = NpmFetcher();
        fetcher.Release = null;

        var finding = (await Audit(Npm, fetcher))["registry"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Contains("must exist before `npm trust`", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_publish_runs_fail()
    {
        var fetcher = NpmFetcher();
        fetcher.Runs = ["failure", "failure", "success"];
        var finding = (await Audit(Npm, fetcher))["runs"];
        Assert.Equal((CheckStatus.Fail, "last 2 completed publish run(s) failed"), (finding.Status, finding.Detail));

        fetcher.Runs = [];
        Assert.Equal(CheckStatus.Skip, (await Audit(Npm, fetcher))["runs"].Status);
        fetcher.Runs = null;
        Assert.Equal("could not read workflow runs", (await Audit(Npm, fetcher))["runs"].Detail);
    }

    [Fact]
    public async Task The_trust_listing_is_only_read_when_asked()
    {
        var fetcher = NpmFetcher();

        Assert.False((await Audit(Npm, fetcher)).ContainsKey("trust"));
        Assert.Equal(0, fetcher.TrustReads);
    }

    [Fact]
    public async Task A_trust_listing_naming_the_old_owner_fails()
    {
        var fetcher = NpmFetcher();
        fetcher.Trust = new([new("rtkelly13/design-system", "publish.yml")]);

        var finding = (await Audit(Npm, fetcher, trust: true))["trust"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Equal("trust config names rtkelly13/design-system publish.yml; expected rtkelly-labs/design-system publish.yml", finding.Detail);
    }

    [Fact]
    public async Task A_trust_listing_matching_repo_and_workflow_is_ok()
    {
        var fetcher = NpmFetcher();
        fetcher.Trust = new([new("rtkelly-labs/design-system", ".github/workflows/Publish.yml")]);
        Assert.Equal(CheckStatus.Ok, (await Audit(Npm, fetcher, trust: true))["trust"].Status);

        // The environment has to match exactly.
        Assert.Equal(CheckStatus.Fail, (await Audit(Npm with { Environment = "npm" }, fetcher, trust: true))["trust"].Status);
    }

    [Fact]
    public async Task A_trust_listing_error_is_a_warning()
    {
        var fetcher = NpmFetcher();
        fetcher.Trust = new([], "exit 1");
        Assert.Equal("npm trust list failed: exit 1", (await Audit(Npm, fetcher, trust: true))["trust"].Detail);

        fetcher.Trust = new([]);
        Assert.Contains("names nothing", (await Audit(Npm, fetcher, trust: true))["trust"].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_nuget_policy_checklist_is_printed_only_under_trust_when_nothing_drifted()
    {
        var found = await Audit(NuGet, NuGetFetcher("https://github.com/rtkelly-labs/design-system.git"));
        Assert.Equal(CheckStatus.Ok, found["provenance"].Status);
        Assert.False(found.ContainsKey("policy"));

        var policy = (await Audit(NuGet, NuGetFetcher("https://github.com/rtkelly-labs/design-system.git"), trust: true))["policy"];
        Assert.Equal(CheckStatus.Manual, policy.Status);
        Assert.Contains("repository owner: rtkelly-labs", policy.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_nuget_release_recording_the_old_owner_warns_to_recreate_the_policy()
    {
        var found = await Audit(NuGet, NuGetFetcher("https://github.com/rtkelly13/design-system.git"));

        Assert.Equal(CheckStatus.Warn, found["provenance"].Status);
        Assert.Contains("nuget.org/account/trustedpublishing", found["provenance"].Fix, StringComparison.Ordinal);
        Assert.Contains("delete the old one", found["provenance"].Fix, StringComparison.Ordinal);
        Assert.Contains("Pkg provenance drifted", found["policy"].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_nuget_manifest_only_warns()
    {
        var fetcher = NuGetFetcher(null);
        fetcher.Files["Directory.Build.props"] = "<RepositoryUrl>https://github.com/rtkelly13/design-system</RepositoryUrl>";

        var found = await Audit(NuGet, fetcher);

        Assert.Equal(CheckStatus.Warn, found["manifest"].Status);
        Assert.Contains("wrong source link", found["manifest"].Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Skip, found["provenance"].Status);
    }
}
