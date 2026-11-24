using Rig.Core.Config;
using Rig.Core.Forks;

namespace Rig.Core.Tests.Forks;

/// <summary>The pure assessment, against the configuration rig ships.</summary>
public sealed class ForkAssessmentTests
{
    private const string Mit = "MIT License\n\nCopyright (c) 2017 Jo Upstream\n\nPermission is hereby granted, free of charge, to any person obtaining a copy\nof this software... THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND";

    private static readonly DateOnly Day = new(2026, 10, 1);

    internal static ForkAssessmentConfig Shipped { get; } = ForkAssessmentConfig.Load(Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config", ForkAssessmentConfig.FileName));

    private static ForkFacts Facts(
        IReadOnlyList<CommitFact>? commits = null,
        IReadOnlyDictionary<string, string>? texts = null,
        IReadOnlyList<string>? extraFiles = null,
        IReadOnlyList<HeaderHit>? hits = null,
        IReadOnlyList<RefFact>? refs = null,
        IReadOnlyList<BlameFact>? blame = null,
        bool shallow = false)
    {
        texts ??= new Dictionary<string, string> { ["LICENSE"] = Mit };
        return new ForkFacts(
            "abc123",
            Day,
            shallow,
            commits ?? [new CommitFact("Jo Upstream", "jo@example.invalid", Day, false)],
            0,
            0,
            [.. texts.Keys.Concat(extraFiles ?? []).Order(StringComparer.Ordinal)],
            texts,
            hits ?? [],
            refs ?? [],
            blame);
    }

    private static Finding? Find(ForkAssessment assessment, string id) => assessment.Findings.FirstOrDefault(f => f.Id == id);

    [Fact]
    public void The_shipped_configuration_loads_and_configures_every_finding()
    {
        Assert.Empty(Shipped.Validate());
        Assert.All(ForkAssessmentConfig.FindingIds, id => Assert.True(Shipped.Findings.ContainsKey(id), id));
    }

    [Fact]
    public void A_configuration_naming_an_unknown_finding_or_a_broken_pattern_is_rejected()
    {
        var broken = Shipped with
        {
            BrandAssets = "(",
            Findings = new Dictionary<string, FindingRule>(Shipped.Findings) { ["made.up"] = new(FindingSeverity.Info, "x") },
        };

        var errors = broken.Validate();

        Assert.Contains(errors, e => e.Contains("'made.up' is unknown", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("does not compile", StringComparison.Ordinal));
    }

    [Fact]
    public void Loading_an_invalid_configuration_throws_a_schema_error()
    {
        using var temp = new TempDir();
        var path = temp.File("fork-assessment.json");
        var json = File.ReadAllText(Path.Join(Path.GetDirectoryName(RepoTemp.Root)!, "config", ForkAssessmentConfig.FileName))
            .Replace("\"single_maintainer_share\": 0.5", "\"single_maintainer_share\": 2", StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var e = Assert.Throws<ConfigLoadException>(() => ForkAssessmentConfig.Load(path));
        Assert.Contains("shares must lie in (0, 1]", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_MIT_project_has_obligations_but_no_blockers()
    {
        var assessment = ForkAssessment.Assess(Shipped, Facts());

        Assert.False(assessment.Blocked);
        Assert.Equal("MIT", Assert.Single(assessment.Licences).Spdx);
        Assert.Equal(FindingSeverity.Obligation, Find(assessment, "licence.terms")!.Severity);
        Assert.Contains("LICENSE: Copyright (c) 2017 Jo Upstream", Find(assessment, "licence.copyright")!.Evidence);
    }

    [Fact]
    public void A_project_without_a_licence_is_blocked()
    {
        var assessment = ForkAssessment.Assess(Shipped, Facts(texts: new Dictionary<string, string> { ["README.md"] = "hello" }));

        Assert.True(assessment.Blocked);
        Assert.Equal(FindingSeverity.Blocker, Find(assessment, "licence.missing")!.Severity);
        Assert.Equal("licence.missing", assessment.Findings[0].Id);
    }

    [Fact]
    public void A_licence_no_rule_recognises_is_blocked()
    {
        var assessment = ForkAssessment.Assess(Shipped, Facts(texts: new Dictionary<string, string> { ["LICENSE"] = "All rights reserved." }));

        Assert.True(assessment.Blocked);
        Assert.Equal(["LICENSE"], Find(assessment, "licence.unrecognised")!.Evidence);
    }

    [Fact]
    public void A_shallow_clone_is_blocked()
    {
        Assert.True(ForkAssessment.Assess(Shipped, Facts(shallow: true)).Blocked);
    }

    [Fact]
    public void Files_under_another_licence_are_third_party_but_the_projects_own_are_not()
    {
        var assessment = ForkAssessment.Assess(Shipped, Facts(
            extraFiles: ["a.thrift", "b.cs"],
            hits: [new HeaderHit("a.thrift", "Apache-2.0"), new HeaderHit("b.cs", "MIT"), new HeaderHit("LICENSE", "Apache-2.0")]));

        var finding = Assert.Single(assessment.Findings, f => f.Id == "third-party.licences");
        Assert.Equal(["a.thrift"], finding.Evidence);
        Assert.Contains("Apache-2.0", finding.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_names_brand_assets_and_vendored_code_are_reported()
    {
        var texts = new Dictionary<string, string>
        {
            ["LICENSE"] = Mit,
            ["src/Lib/Lib.csproj"] = "<Project><PropertyGroup><PackageId>Upstream.Lib</PackageId></PropertyGroup></Project>",
            ["src/Gen/Gen.csproj"] = "<Project><PropertyGroup><PackageId>$(Name)</PackageId></PropertyGroup></Project>",
        };

        var assessment = ForkAssessment.Assess(Shipped, Facts(texts: texts, extraFiles: ["src/icon.png", "third_party/zlib/zlib.h"]));

        Assert.Equal(["nuget: Upstream.Lib"], Find(assessment, "names.packages")!.Evidence);
        Assert.Equal(["src/icon.png"], Find(assessment, "names.brand-assets")!.Evidence);
        Assert.NotNull(Find(assessment, "third-party.vendored"));
    }

    [Fact]
    public void An_AI_policy_in_upstreams_templates_is_quoted()
    {
        var texts = new Dictionary<string, string>
        {
            ["LICENSE"] = Mit,
            [".github/ISSUE_TEMPLATE/bug.yml"] = "body:\n  - This project does not accept LLM generated pull requests.\n  - Describe the bug.\n",
        };

        var finding = Find(ForkAssessment.Assess(Shipped, Facts(texts: texts)), "contribution.ai-policy");

        Assert.Equal([".github/ISSUE_TEMPLATE/bug.yml: - This project does not accept LLM generated pull requests."], finding!.Evidence);
    }

    [Fact]
    public void Identities_merge_on_a_shared_email_or_name_but_never_on_an_ignored_name()
    {
        CommitFact[] commits =
        [
            new("Jo Upstream", "jo@home.invalid", Day, false),
            new("Jo Upstream", "jo@work.invalid", Day, false),
            new("jo", "jo@work.invalid", Day, false),
            new("dependabot[bot]", "a@bots.invalid", Day, false),
            new("dependabot[bot]", "b@bots.invalid", Day, false),
        ];

        var people = ForkAssessment.Assess(Shipped, Facts(commits: commits)).People;

        Assert.Equal(5, people.Identities);
        Assert.Equal(3, people.Contributors);
        var jo = people.Frequent[0];
        Assert.Equal("Jo Upstream", jo.Name);
        Assert.Equal(["jo"], jo.Aliases);
        Assert.Equal(["jo@home.invalid", "jo@work.invalid"], jo.Emails);
        Assert.Equal(3, jo.Commits);
    }

    [Fact]
    public void One_dominant_author_is_a_single_maintainer_with_a_bus_factor_of_one()
    {
        CommitFact[] commits =
        [
            .. Enumerable.Range(0, 8).Select(i => new CommitFact("Jo", "jo@example.invalid", Day.AddDays(-i * 100), false)),
            new("Sam", "sam@example.invalid", Day.AddDays(-2000), true),
            new("Kim", "kim@example.invalid", Day.AddDays(-10), false),
        ];

        var assessment = ForkAssessment.Assess(Shipped, Facts(commits: commits));

        Assert.Equal(1, assessment.People.BusFactor);
        Assert.Equal(FindingSeverity.Risk, Find(assessment, "people.single-maintainer")!.Severity);
        // Within the 365 days before the revision: Jo four times (days 0, 100, 200, 300), Kim once.
        Assert.Equal(5, assessment.People.RecentCommits);
        Assert.Equal(0.8, assessment.People.RecentTopShare, 3);
        Assert.NotNull(Find(assessment, "people.recent-single-maintainer"));
        Assert.Equal("1 of 10 commits are signed off", Find(assessment, "contribution.dco")!.Title);
        Assert.Equal([2021, 2024, 2025, 2026], assessment.People.Years.Select(y => y.Year));
    }

    [Fact]
    public void Shared_work_is_not_a_single_maintainer()
    {
        CommitFact[] commits =
        [
            new("Jo", "jo@example.invalid", Day, false),
            new("Sam", "sam@example.invalid", Day, false),
            new("Kim", "kim@example.invalid", Day, false),
        ];

        var assessment = ForkAssessment.Assess(Shipped, Facts(commits: commits));

        Assert.Null(Find(assessment, "people.single-maintainer"));
        Assert.Equal(2, assessment.People.BusFactor);
    }

    [Fact]
    public void Unmerged_refs_and_line_ownership_are_reported_largest_first()
    {
        var assessment = ForkAssessment.Assess(Shipped, Facts(
            refs: [new RefFact("origin/small", 1, Day), new RefFact("origin/big", 9, Day)],
            blame: [new BlameFact("Jo Upstream", "jo@example.invalid", 90), new BlameFact("Sam", "sam@example.invalid", 10)]));

        Assert.Equal(["origin/big: 9 commit(s), last 2026-10-01", "origin/small: 1 commit(s), last 2026-10-01"], Find(assessment, "history.unmerged-refs")!.Evidence);
        Assert.Equal(100, assessment.Ownership!.Lines);
        Assert.Equal("Jo Upstream: 90 lines (90%)", Find(assessment, "ownership.concentration")!.Evidence[0]);
    }

    [Fact]
    public void The_report_does_not_depend_on_the_order_facts_arrive_in()
    {
        CommitFact[] commits =
        [
            new("Jo", "jo@example.invalid", Day, false),
            new("Sam", "sam@example.invalid", Day.AddDays(-400), false),
            new("Jo", "jo@other.invalid", Day.AddDays(-800), false),
            new("Kim", "kim@example.invalid", Day, false),
        ];

        var forward = ForkAssessment.Assess(Shipped, Facts(commits: commits)).ToJson();
        var backward = ForkAssessment.Assess(Shipped, Facts(commits: [.. commits.Reverse()])).ToJson();

        Assert.Equal(forward, backward);
        Assert.Contains("\"revision\": \"abc123\"", forward, StringComparison.Ordinal);
        Assert.Contains("\"severity\": \"obligation\"", forward, StringComparison.Ordinal);
    }
}
