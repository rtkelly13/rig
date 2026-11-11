using Rig.Core.Packages;
using Rig.Core.Tests.Fakes;

namespace Rig.Core.Tests.Packages;

/// <summary>
/// <see cref="NuGetGovernance"/> and the NuGet half of <see cref="PackageAudit"/>: the publish workflow's login job, the
/// registry judgements and the policy checklist. Ported from <c>tests/test_workspace_nuget_governance.py</c>.
/// </summary>
public sealed class NuGetGovernanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
    private const string Repo = "git@github.com:rtkelly-labs/Parquet.SourceGenerator.git";
    private static readonly RepoIdentity Live = new("rtkelly-labs/Parquet.SourceGenerator", 467247054, 330611343);
    private const string Source = "https://github.com/rtkelly-labs/Parquet.SourceGenerator.git";

    private static readonly PackageSpec Spec = new(PackageSpec.NuGet, "Parquet.SourceGenerator", "Directory.Build.props", "release.yml")
    {
        NuGetOwner = "rtkelly-labs",
        NuGetUser = "rtk_13",
        Owners = ["rtk_13", "rtkelly-labs"],
        Scopes = ["push-new", "push-version"],
    };

    private static readonly string[] SharedPolicy = ["Parquet.SourceGenerator", "Parquet.SourceGenerator.Attributes"];

    private static readonly Release Release0 = new("0.0.3", null) { SourceRepoUrl = Source, Owners = ["rtk_13", "rtkelly-labs"] };

    // Trimmed from Parquet.SourceGenerator's release.yml: the build job holds read only, and the publish job alone gets
    // id-token: write, runs in `release`, logs in and pushes.
    private const string ReleaseYml = """
        name: Release & Publish NuGet
        on:
          workflow_dispatch:
        permissions:
          contents: read
        jobs:
          build:
            runs-on: ubuntu-latest
            permissions:
              contents: read
            steps:
              - run: dotnet pack --output ./artifacts
          publish:
            needs: [build]
            runs-on: ubuntu-latest
            permissions:
              contents: write
              id-token: write
            environment: release
            steps:
              - name: Login to NuGet via OIDC (Trusted Publishing)
                uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1
                id: login
                with:
                  user: ${{ secrets.NUGET_USER || 'rtk_13' }}
              - name: Push to NuGet
                env:
                  NUGET_API_KEY: ${{ steps.login.outputs.NUGET_API_KEY }}
                run: |
                  dotnet nuget push ./artifacts/*.nupkg \
                    --api-key "$NUGET_API_KEY" \
                    --source https://api.nuget.org/v3/index.json \
                    --skip-duplicate

        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Yml(string from, string to) => ReleaseYml.ReplaceLineEndings("\n").Replace(from, to, StringComparison.Ordinal);

    private static FakePackageFetcher Fetcher(string? workflow = null, Release? release = null, bool noRelease = false) => new()
    {
        Repo = Live,
        Files =
        {
            ["Directory.Build.props"] = $"<RepositoryUrl>{Source}</RepositoryUrl>",
            [".github/workflows/release.yml"] = workflow ?? ReleaseYml.ReplaceLineEndings("\n"),
        },
        Release = noRelease ? null : release ?? Release0,
    };

    private static Task<IReadOnlyList<Finding>> Findings(FakePackageFetcher fetcher, IEnumerable<PackageSpec> specs, bool trust = false) =>
        PackageAudit.AuditAsync([.. specs.Select(spec => new PackageBinding("Parquet.SourceGenerator", Repo, spec))], fetcher, Now, trust, Token);

    private static async Task<Dictionary<string, Finding>> Audit(FakePackageFetcher fetcher, PackageSpec? spec = null, bool trust = false)
    {
        var byCheck = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var finding in await Findings(fetcher, [spec ?? Spec], trust))
        {
            byCheck[finding.Check] = finding;
        }

        return byCheck;
    }

    [Fact]
    public void The_release_workflow_reads_as_the_publish_job()
    {
        var workflow = NuGetGovernance.ReadPublishWorkflow(ReleaseYml.ReplaceLineEndings("\n"));

        var job = Assert.Single(Assert.IsType<PublishWorkflow>(workflow).LoginJobs);
        Assert.Equal(("publish", true, "rtk_13", "release"), (job.Name, job.IdTokenWrite, job.User, job.Environment));
        Assert.True(job.PushFound);
        Assert.True(job.SkipDuplicate);
        Assert.Empty(job.PushSecrets);
    }

    [Fact]
    public async Task The_release_workflow_passes_every_login_check()
    {
        var found = await Audit(Fetcher());

        Assert.Equal(CheckStatus.Ok, found["login"].Status);
        Assert.Equal(CheckStatus.Ok, found["push"].Status);

        // No environment declared: a policy without one matches any, so this is noted, not judged.
        Assert.Equal(CheckStatus.Skip, found["environment"].Status);
        Assert.Equal(CheckStatus.Ok, (await Audit(Fetcher(), Spec with { Environment = "release" }))["environment"].Status);
    }

    [Fact]
    public async Task Id_token_on_another_job_fails()
    {
        var moved = Yml("      contents: read\n    steps:", "      contents: read\n      id-token: write\n    steps:")
            .Replace("      contents: write\n      id-token: write\n", "      contents: write\n", StringComparison.Ordinal);

        var finding = (await Audit(Fetcher(moved)))["login"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("id-token", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_all_and_inherited_permissions_grant_id_token()
    {
        var writeAll = Yml("    permissions:\n      contents: write\n      id-token: write\n", "    permissions: write-all\n");
        Assert.Contains("permissions: write-all", writeAll, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Ok, (await Audit(Fetcher(writeAll)))["login"].Status);

        const string inherited = "permissions:\n  id-token: write\njobs:\n  p:\n    steps:\n      - uses: nuget/login@v1\n        with:\n          user: rtk_13\n";
        Assert.True(NuGetGovernance.ReadPublishWorkflow(inherited)!.LoginJobs[0].IdTokenWrite);
    }

    [Fact]
    public async Task A_login_user_mismatch_fails_but_an_org_policy_owner_is_not_compared()
    {
        var finding = (await Audit(Fetcher(Yml("'rtk_13'", "'someone-else'"))))["login"];
        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("expected rtk_13", finding.Detail, StringComparison.Ordinal);

        // nuget_owner is the organisation; logging in as the member account is correct.
        Assert.Equal(CheckStatus.Ok, (await Audit(Fetcher()))["login"].Status);
    }

    [Fact]
    public async Task A_login_user_without_a_literal_cannot_be_checked()
    {
        var secretOnly = Yml("${{ secrets.NUGET_USER || 'rtk_13' }}", "${{ secrets.NUGET_USER }}");
        Assert.Equal(CheckStatus.Warn, (await Audit(Fetcher(secretOnly)))["login"].Status);
    }

    [Fact]
    public async Task An_undeclared_login_user_is_skipped() =>
        Assert.Equal(CheckStatus.Skip, (await Audit(Fetcher(), Spec with { NuGetUser = null }))["login"].Status);

    [Fact]
    public async Task An_environment_mismatch_fails()
    {
        var finding = (await Audit(Fetcher(Yml("environment: release", "environment: { name: staging }")), Spec with { Environment = "release" }))["environment"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("staging", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_without_skip_duplicate_warns()
    {
        var finding = (await Audit(Fetcher(Yml(" \\\n            --skip-duplicate", string.Empty))))["push"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Contains("--skip-duplicate", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_push_reading_a_long_lived_key_warns()
    {
        var finding = (await Audit(Fetcher(Yml("${{ steps.login.outputs.NUGET_API_KEY }}", "${{ secrets.NUGET_API_KEY }}"))))["push"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Contains("NUGET_API_KEY", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_login_names_the_called_reusable_workflow()
    {
        const string caller = "on: workflow_dispatch\njobs:\n  release:\n    uses: ./.github/workflows/publish-nuget.yml@main\n";

        var finding = (await Audit(Fetcher(caller)))["login"];

        Assert.Equal(CheckStatus.Fail, finding.Status);
        Assert.Contains("publish-nuget.yml", finding.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unparseable_workflow_fails_the_login_check() =>
        Assert.Equal(CheckStatus.Fail, (await Audit(Fetcher("jobs: [unclosed")))["login"].Status);

    [Fact]
    public async Task A_healthy_release_has_no_policy_checklist()
    {
        var found = await Audit(Fetcher());

        Assert.Equal(CheckStatus.Ok, found["registry"].Status);
        Assert.Equal(CheckStatus.Ok, found["owners"].Status);
        Assert.Equal(CheckStatus.Ok, found["provenance"].Status);
        Assert.DoesNotContain("policy", found.Keys);
        Assert.DoesNotContain(found.Keys, key => key is "listed" or "vulnerabilities" or "deprecation");
    }

    [Fact]
    public async Task A_missing_package_warns_and_prints_the_checklist()
    {
        var found = await Audit(Fetcher(noRelease: true));

        Assert.Equal(CheckStatus.Warn, found["registry"].Status);
        Assert.Contains("push-new", found["registry"].Detail, StringComparison.Ordinal);
        Assert.Contains("Parquet.SourceGenerator registry drifted", found["policy"].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_owner_mismatch_is_advisory_because_search_lags()
    {
        var found = await Audit(Fetcher(release: Release0 with { Owners = ["rtk_13"] }));

        Assert.Equal(CheckStatus.Warn, found["owners"].Status);
        Assert.Contains("missing rtkelly-labs", found["owners"].Detail, StringComparison.Ordinal);
        Assert.Contains("as of the last publish", found["owners"].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("policy", found.Keys);
    }

    [Fact]
    public async Task Unreadable_owners_are_skipped() =>
        Assert.Equal(CheckStatus.Skip, (await Audit(Fetcher(release: Release0 with { Owners = null })))["owners"].Status);

    [Fact]
    public async Task A_stale_repository_url_reports_the_old_owner()
    {
        var found = await Audit(Fetcher(release: Release0 with { SourceRepoUrl = "https://github.com/rtkelly13/Parquet.SourceGenerator.git" }));

        Assert.Equal(CheckStatus.Warn, found["provenance"].Status);
        Assert.Contains("records rtkelly13/Parquet.SourceGenerator", found["provenance"].Detail, StringComparison.Ordinal);
        Assert.Contains("Parquet.SourceGenerator provenance drifted", found["policy"].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vulnerable_latest_fails_and_deprecation_is_reported()
    {
        var found = await Audit(Fetcher(release: Release0 with { Vulnerabilities = ["high https://github.com/advisories/GHSA-x"], Deprecation = "Legacy" }));

        Assert.Equal(CheckStatus.Fail, found["vulnerabilities"].Status);
        Assert.Contains("GHSA-x", found["vulnerabilities"].Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Warn, found["deprecation"].Status);
    }

    [Theory]
    [InlineData("0.2.0-alpha.cipr003")]
    [InlineData("0.0.6-alpha.cipr037+dynamic-du001")]
    [InlineData("1.0.0+sha.abc")]
    public async Task A_listed_ci_build_as_newest_warns(string version)
    {
        var finding = (await Audit(Fetcher(release: Release0 with { Version = version })))["listed"];

        Assert.Equal(CheckStatus.Warn, finding.Status);
        Assert.Contains($"/{version.Split('+')[0]}/Manage", finding.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_all_unlisted_package_warns()
    {
        var found = await Audit(Fetcher(release: Release0 with { Listed = false }));

        Assert.Equal(CheckStatus.Warn, found["registry"].Status);
        Assert.DoesNotContain("provenance", found.Keys);
    }

    [Theory]
    [InlineData("Parquet.SourceGenerator", "Parquet.SourceGenerator")]
    [InlineData("Parquet.SourceGenerator,Parquet.SourceGenerator.Attributes,Parquet.SourceGenerator.Legacy", "Parquet.SourceGenerator*")]
    [InlineData("Iceberg.Net.Core,Iceberg.Net.Arrow", "Iceberg.Net.*")]
    [InlineData("Resultful,FluentAssertions.OneOf", "Resultful, FluentAssertions.OneOf")]
    public void The_package_glob_covers_every_id(string ids, string glob) => Assert.Equal(glob, NuGetGovernance.PackageGlob(ids.Split(',')));

    [Fact]
    public void The_checklist_renders_every_policy_field()
    {
        var checklist = new PolicyChecklist("rtkelly-labs", "rtkelly-labs", "Parquet.SourceGenerator", ".github/workflows/release.yml", null, ["push-new"], ["P"]);

        Assert.Equal(
            [
                "policy owner (nuget.org user or organisation): rtkelly-labs",
                "repository owner: rtkelly-labs",
                "repository: Parquet.SourceGenerator",
                "workflow file: release.yml",
                "environment: (none)",
                "scopes: push-new",
                "packages: P",
                "at: https://www.nuget.org/account/trustedpublishing",
                "note: temporary until first publish, 7 days; after a transfer add the new policy, publish, then delete the old one",
            ],
            checklist.Render().Split('\n'));
    }

    [Fact]
    public void An_undeclared_policy_owner_and_scopes_are_asked_for()
    {
        var lines = new PolicyChecklist(null, "o", "r", "w.yml", "release", [], ["P"]).Render().Split('\n');

        Assert.Contains("policy owner (nuget.org user or organisation): ? (declare nuget_owner)", lines);
        Assert.Contains("scopes: ? (declare scopes)", lines);
        Assert.Contains("environment: release", lines);
    }

    [Fact]
    public async Task Trust_prints_one_checklist_per_shared_policy()
    {
        var specs = SharedPolicy.Select(name => Spec with { Name = name });

        var policy = Assert.Single(await Findings(Fetcher(), specs, trust: true), f => f.Check == "policy");

        Assert.Equal(("nuget:Parquet.SourceGenerator*", CheckStatus.Manual), (policy.Package, policy.Status));
        Assert.Contains("confirm it by hand", policy.Detail, StringComparison.Ordinal);
        Assert.Contains("scopes: push-new, push-version", policy.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_auth_packages_get_no_checklist() =>
        Assert.DoesNotContain("policy", (await Audit(Fetcher(noRelease: true), Spec with { Auth = PackageSpec.Token }, trust: true)).Keys);
}
