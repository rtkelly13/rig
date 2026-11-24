using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rig.Core.Config;

namespace Rig.Core.Forks;

/// <summary>How much a finding asks of the person forking.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FindingSeverity>))]
public enum FindingSeverity
{
    /// <summary>Do not fork until it is resolved; the command exits 1.</summary>
    [JsonStringEnumMemberName("blocker")]
    Blocker,

    /// <summary>Something the fork must do, every time it publishes.</summary>
    [JsonStringEnumMemberName("obligation")]
    Obligation,

    /// <summary>Something that may go wrong and should be planned for.</summary>
    [JsonStringEnumMemberName("risk")]
    Risk,

    /// <summary>A fact worth knowing.</summary>
    [JsonStringEnumMemberName("info")]
    Info,
}

/// <summary>One licence the assessment recognises.</summary>
/// <param name="Spdx">Its SPDX identifier.</param>
/// <param name="Family">permissive, weak-copyleft, copyleft, network-copyleft or public-domain.</param>
/// <param name="AllOf">Phrases a licence file must all contain, case-insensitively, to be this licence. The first entry that matches wins.</param>
/// <param name="HeaderMarkers">Phrases that mark a source file's own header as under this licence.</param>
/// <param name="Obligations">What a fork must do under it.</param>
public sealed record LicenceRule(string Spdx, string Family, IReadOnlyList<string> AllOf, IReadOnlyList<string> HeaderMarkers, IReadOnlyList<string> Obligations);

/// <summary>Where a package manifest declares the name it is published under.</summary>
/// <param name="Kind">The registry: nuget, npm, pypi, crates.</param>
/// <param name="Path">A pattern matching the manifest's path.</param>
/// <param name="Name">A pattern whose first group is the published name.</param>
public sealed record PackageManifestRule(string Kind, string Path, string Name);

/// <summary>Phrases that signal a policy or a state in upstream's own documents.</summary>
/// <param name="AiPolicy">Upstream says something about AI or LLM use.</param>
/// <param name="Cla">Upstream asks for a contributor licence agreement.</param>
/// <param name="Unmaintained">Upstream says it has ended.</param>
public sealed record MarkerConfig(IReadOnlyList<string> AiPolicy, IReadOnlyList<string> Cla, IReadOnlyList<string> Unmaintained);

/// <summary>How author identities are merged.</summary>
/// <param name="IgnoredNames">Names that never merge two identities, because many people or bots share them.</param>
public sealed record IdentityConfig(IReadOnlyList<string> IgnoredNames);

/// <summary>The numbers findings are judged against.</summary>
/// <param name="SingleMaintainerShare">The share of commits above which one identity is a single maintainer.</param>
/// <param name="RecentWindowDays">How far before the assessed revision counts as recent.</param>
/// <param name="BusFactorCoverage">The share of commits the bus factor's identities must cover together.</param>
/// <param name="FrequentContributors">How many contributors the report lists.</param>
/// <param name="EvidenceLimit">How many paths or lines a finding quotes.</param>
public sealed record AssessmentThresholds(double SingleMaintainerShare, int RecentWindowDays, double BusFactorCoverage, int FrequentContributors, int EvidenceLimit);

/// <summary>What a finding asks, as configured.</summary>
/// <param name="Severity">How much it asks.</param>
/// <param name="Responsibility">What the person forking must do about it.</param>
public sealed record FindingRule(FindingSeverity Severity, string Responsibility);

/// <summary><c>config/fork-assessment.json</c>: everything <c>rig fork assess</c> looks for, and what each finding asks.</summary>
/// <param name="Description">What the file is for.</param>
/// <param name="Licences">The licences recognised, most specific first.</param>
/// <param name="LicenceFiles">Root file names that hold the project's licence.</param>
/// <param name="NoticeFiles">Root file names that hold an attribution notice.</param>
/// <param name="TrademarkFiles">Paths that hold a trademark policy.</param>
/// <param name="PolicyFiles">Paths whose text states upstream's policies.</param>
/// <param name="ReadmeFiles">Paths whose text says whether upstream is maintained.</param>
/// <param name="BrandAssets">Paths of logos and icons.</param>
/// <param name="VendoredDirectories">Paths inside vendored third-party code.</param>
/// <param name="IgnoredPaths">Paths never read.</param>
/// <param name="PackageManifests">Where published names are declared.</param>
/// <param name="Markers">Phrases looked for in policy files and READMEs.</param>
/// <param name="Identity">How identities are merged.</param>
/// <param name="BlamePaths">Paths whose lines <c>--blame</c> attributes.</param>
/// <param name="Thresholds">The numbers findings are judged against.</param>
/// <param name="Findings">Each finding's severity and responsibility, by id.</param>
public sealed record ForkAssessmentConfig(
    string Description,
    IReadOnlyList<LicenceRule> Licences,
    string LicenceFiles,
    string NoticeFiles,
    string TrademarkFiles,
    string PolicyFiles,
    string ReadmeFiles,
    string BrandAssets,
    string VendoredDirectories,
    string IgnoredPaths,
    IReadOnlyList<PackageManifestRule> PackageManifests,
    MarkerConfig Markers,
    IdentityConfig Identity,
    string BlamePaths,
    AssessmentThresholds Thresholds,
    IReadOnlyDictionary<string, FindingRule> Findings)
{
    /// <summary>The configuration's file name under <c>config/</c>.</summary>
    public const string FileName = "fork-assessment.json";

    /// <summary>Every finding id the assessment can raise; each must be configured.</summary>
    public static readonly IReadOnlyList<string> FindingIds =
    [
        "history.shallow", "licence.missing", "licence.unrecognised", "licence.terms", "licence.copyright", "notice.carry",
        "third-party.licences", "third-party.vendored", "names.packages", "names.brand-assets", "names.trademark",
        "contribution.ai-policy", "contribution.cla", "contribution.dco", "people.single-maintainer",
        "people.recent-single-maintainer", "people.personal-data", "activity.ended", "history.unmerged-refs",
        "ownership.concentration",
    ];

    /// <summary>Loads and validates <paramref name="path"/>.</summary>
    /// <exception cref="ConfigLoadException">The file is missing, malformed, or fails validation.</exception>
    public static ForkAssessmentConfig Load(string path)
    {
        var config = ConfigLoader.Load(path, ForkAssessmentJsonContext.Default.ForkAssessmentConfig);
        var errors = config.Validate();
        return errors.Count == 0
            ? config
            : throw new ConfigLoadException(ConfigLoadError.Schema, $"{path}: {string.Join("; ", errors)}", path);
    }

    /// <summary>What is wrong with this configuration: unknown or missing findings, and patterns that do not compile.</summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors =
        [
            .. FindingIds.Where(id => !Findings.ContainsKey(id)).Select(id => $"finding '{id}' is not configured"),
            .. Findings.Keys.Where(id => !FindingIds.Contains(id, StringComparer.Ordinal)).Order(StringComparer.Ordinal).Select(id => $"finding '{id}' is unknown"),
            .. Licences.Where(l => l.AllOf.Count == 0).Select(l => $"licence '{l.Spdx}' has no all_of phrases"),
        ];
        string[] patterns = [LicenceFiles, NoticeFiles, TrademarkFiles, PolicyFiles, ReadmeFiles, BrandAssets, VendoredDirectories, IgnoredPaths, BlamePaths, .. PackageManifests.SelectMany(m => new[] { m.Path, m.Name })];
        foreach (var pattern in patterns)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException e)
            {
                errors.Add($"pattern '{pattern}' does not compile: {e.Message}");
            }
        }

        if (Thresholds.SingleMaintainerShare is <= 0 or > 1 || Thresholds.BusFactorCoverage is <= 0 or > 1)
        {
            errors.Add("shares must lie in (0, 1]");
        }

        if (Thresholds.RecentWindowDays <= 0 || Thresholds.FrequentContributors <= 0 || Thresholds.EvidenceLimit <= 0)
        {
            errors.Add("day and count thresholds must be positive");
        }

        return errors;
    }
}
