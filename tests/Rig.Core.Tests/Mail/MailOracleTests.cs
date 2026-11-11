using System.Text.Json.Nodes;
using Rig.Core.Mail;

namespace Rig.Core.Tests.Mail;

/// <summary>
/// Holds <c>Rig.Core.Mail</c> to <c>rig.mail</c>. <c>mail-oracle.json</c> records, for two placeholder rules files, every
/// rule's match and sweep query, the filter XML (stamped 2026-11-04T09:30:05Z), the sweep plan and API filters JSON, the
/// whole Apps Script byte for byte, and the coverage of a saved sample page, plus the search form of a few label names.
/// </summary>
public sealed class MailOracleTests : IDisposable
{
    private static readonly JsonObject Oracle =
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mail", "mail-oracle.json")))!.AsObject();

    private static readonly DateTimeOffset Stamp = new(2026, 11, 4, 9, 30, 5, TimeSpan.Zero);

    private readonly TempDir temp = new();

    public static TheoryData<int> Cases => [.. Enumerable.Range(0, Oracle["cases"]!.AsArray().Count)];

    public void Dispose() => temp.Dispose();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Queries_match_the_python(int index)
    {
        var (ruleset, expected) = Case(index);

        var queries = expected["queries"]!.AsArray();

        Assert.Equal(queries.Count, ruleset.Rules.Count);
        foreach (var (rule, query) in ruleset.Rules.Zip(queries))
        {
            Assert.Equal(query!["rule"]!.GetValue<string>(), rule.Name);
            Assert.Equal(query["match"]!.GetValue<string>(), MailRuleSet.MatchQuery(rule.Match));
            Assert.Equal(query["sweep"]!.GetValue<string>(), MailRuleSet.SweepQuery(rule));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Exports_match_the_python(int index)
    {
        var (ruleset, expected) = Case(index);

        Assert.Equal(expected["filters_xml"]!.GetValue<string>(), MailExport.FiltersXml(ruleset, Stamp));
        Assert.Equal(expected["sweep"]!.GetValue<string>(), MailExport.SweepJson(MailExport.SweepPlan(ruleset)));
        Assert.Equal(expected["api_filters"]!.GetValue<string>(), MailExport.ApiFiltersJson(MailExport.ApiFilters(ruleset)));
        Assert.Equal(expected["apps_script"]!.GetValue<string>(), AppsScript.Render(MailExport.SweepPlan(ruleset), MailExport.ApiFilters(ruleset)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Coverage_matches_the_python(int index)
    {
        var (ruleset, expected) = Case(index);
        var page = temp.File("page.json");
        File.WriteAllText(page, Oracle["page"]!.GetValue<string>());

        var rows = MailCoverage.Coverage(ruleset, MailCoverage.ReadSample([page]));

        Assert.Equal(expected["coverage"]!.GetValue<string>(), MailCoverage.CoverageJson(rows));
    }

    [Fact]
    public void Search_labels_match_the_python()
    {
        foreach (var (name, label) in Oracle["labels"]!.AsObject())
        {
            Assert.Equal(label!.GetValue<string>(), MailRuleSet.SearchLabel(name));
        }
    }

    private static (MailRuleSet Rules, JsonObject Expected) Case(int index)
    {
        var expected = Oracle["cases"]![index]!.AsObject();
        return (MailRuleSet.Parse(expected["rules"]!.GetValue<string>()), expected);
    }
}
