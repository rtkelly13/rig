using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Core.Forks;

/// <summary>One thing the person forking must know or do.</summary>
/// <param name="Id">The stable finding id, as configured.</param>
/// <param name="Severity">How much it asks.</param>
/// <param name="Title">What was found.</param>
/// <param name="Evidence">The paths, lines or numbers that show it.</param>
/// <param name="Responsibility">What the person forking must do about it.</param>
public sealed record Finding(string Id, FindingSeverity Severity, string Title, IReadOnlyList<string> Evidence, string Responsibility);

/// <summary>A licence file and what it was recognised as.</summary>
/// <param name="Path">The file.</param>
/// <param name="Spdx">The licence, or null when no rule matched.</param>
/// <param name="Family">Its family, or null.</param>
public sealed record LicenceFile(string Path, string? Spdx, string? Family);

/// <summary>One person, after merging the names and emails they committed under.</summary>
/// <param name="Name">The name they used most.</param>
/// <param name="Aliases">Every other name.</param>
/// <param name="Emails">Every email.</param>
/// <param name="Commits">Non-merge commits.</param>
/// <param name="Share">Their share of all non-merge commits.</param>
/// <param name="FirstYear">The year of their first commit.</param>
/// <param name="LastYear">The year of their last commit.</param>
/// <param name="ActiveYears">How many distinct years they committed in.</param>
public sealed record Contributor(string Name, IReadOnlyList<string> Aliases, IReadOnlyList<string> Emails, int Commits, double Share, int FirstYear, int LastYear, int ActiveYears);

/// <summary>One year's activity.</summary>
/// <param name="Year">The year.</param>
/// <param name="Commits">Non-merge commits.</param>
/// <param name="Contributors">Distinct people.</param>
/// <param name="Leaders">The busiest three, as "name (commits)".</param>
public sealed record YearActivity(int Year, int Commits, int Contributors, IReadOnlyList<string> Leaders);

/// <summary>Who wrote the history.</summary>
/// <param name="Commits">Non-merge commits.</param>
/// <param name="Merges">Merge commits.</param>
/// <param name="Tags">Tags reachable from the revision.</param>
/// <param name="Contributors">Distinct people, identities merged.</param>
/// <param name="Identities">Distinct name and email pairs before merging.</param>
/// <param name="FirstCommit">The first commit's date.</param>
/// <param name="BusFactor">The fewest people whose commits together reach the configured coverage.</param>
/// <param name="RecentCommits">Commits within the recent window before the revision.</param>
/// <param name="RecentTopShare">The busiest person's share of those.</param>
/// <param name="Frequent">The most frequent contributors, busiest first.</param>
/// <param name="Years">Activity per year, oldest first.</param>
public sealed record PeopleSummary(
    int Commits,
    int Merges,
    int Tags,
    int Contributors,
    int Identities,
    DateOnly? FirstCommit,
    int BusFactor,
    int RecentCommits,
    double RecentTopShare,
    IReadOnlyList<Contributor> Frequent,
    IReadOnlyList<YearActivity> Years);

/// <summary>One person's share of the lines at the revision.</summary>
/// <param name="Name">The person.</param>
/// <param name="Lines">Lines they last changed.</param>
/// <param name="Share">Their share of the blamed lines.</param>
public sealed record LineShare(string Name, int Lines, double Share);

/// <summary>Whose lines the revision holds (only with <c>--blame</c>).</summary>
/// <param name="Lines">The lines attributed.</param>
/// <param name="Leaders">The people holding most lines, most first.</param>
public sealed record OwnershipSummary(int Lines, IReadOnlyList<LineShare> Leaders);

/// <summary>The report <c>rig fork assess</c> prints: the facts summarised, then the findings.</summary>
/// <param name="Revision">The assessed commit.</param>
/// <param name="RevisionDate">Its commit date.</param>
/// <param name="Licences">The project's licence files.</param>
/// <param name="People">Who wrote the history.</param>
/// <param name="Ownership">Whose lines the revision holds, when blamed.</param>
/// <param name="Findings">Every finding, most severe first.</param>
public sealed record ForkAssessment(
    string Revision,
    DateOnly RevisionDate,
    IReadOnlyList<LicenceFile> Licences,
    PeopleSummary People,
    OwnershipSummary? Ownership,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>Whether any finding blocks the fork.</summary>
    public bool Blocked => Findings.Any(f => f.Severity == FindingSeverity.Blocker);

    /// <summary>The report as indented JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ForkAssessmentJsonContext.Default.ForkAssessment);

    /// <summary>
    /// Assesses <paramref name="facts"/> against <paramref name="config"/>. Pure: the same facts and configuration always
    /// give the same report, in the same order.
    /// </summary>
    public static ForkAssessment Assess(ForkAssessmentConfig config, ForkFacts facts)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(facts);
        var limit = config.Thresholds.EvidenceLimit;
        List<Finding> findings = [];
        void Add(string id, string title, IEnumerable<string> evidence)
        {
            var rule = config.Findings[id];
            findings.Add(new Finding(id, rule.Severity, title, [.. evidence], rule.Responsibility));
        }

        if (facts.Shallow)
        {
            Add("history.shallow", "The clone's history is shallow, so authorship and history counts are incomplete", []);
        }

        var licences = ReadLicences(config, facts);
        LicenceFindings(config, facts, licences, Add);
        NameFindings(config, facts, limit, Add);
        ContributionFindings(config, facts, limit, Add);

        var identities = Identities.Build(config, facts);
        var people = SummarisePeople(config, facts, identities);
        PeopleFindings(config, facts, people, Add);

        var unmerged = facts.Refs.OrderByDescending(r => r.UniqueCommits).ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        if (unmerged.Count > 0)
        {
            Add(
                "history.unmerged-refs",
                $"{unmerged.Count} ref(s) hold {unmerged.Sum(r => r.UniqueCommits)} commit(s) the assessed revision does not",
                unmerged.Take(limit).Select(r => $"{r.Name}: {r.UniqueCommits} commit(s), last {r.LastDate:yyyy-MM-dd}"));
        }

        var ownership = SummariseOwnership(facts, identities);
        if (ownership is { Leaders.Count: > 0 })
        {
            var top = ownership.Leaders[0];
            Add(
                "ownership.concentration",
                $"{top.Name} last changed {Percent(top.Share)} of the {ownership.Lines} lines attributed",
                ownership.Leaders.Take(limit).Select(l => $"{l.Name}: {l.Lines} lines ({Percent(l.Share)})"));
        }

        var ordered = findings
            .Select((finding, index) => (finding, index))
            .OrderBy(pair => pair.finding.Severity)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.finding)
            .ToList();
        return new ForkAssessment(facts.Revision, facts.RevisionDate, licences, people, ownership, ordered);
    }

    /// <summary>A share as a whole-number percentage, culture-invariant.</summary>
    public static string Percent(double share) => (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static IReadOnlyList<LicenceFile> ReadLicences(ForkAssessmentConfig config, ForkFacts facts)
    {
        var pattern = ForkFactsReader.Pattern(config.LicenceFiles);
        return [.. facts.Files.Where(path => pattern.IsMatch(path)).Select(path =>
        {
            var text = Normalise(facts.Texts.GetValueOrDefault(path, string.Empty));
            var rule = config.Licences.FirstOrDefault(l => l.AllOf.All(phrase => text.Contains(Normalise(phrase), StringComparison.OrdinalIgnoreCase)));
            return new LicenceFile(path, rule?.Spdx, rule?.Family);
        })];
    }

    private static void LicenceFindings(ForkAssessmentConfig config, ForkFacts facts, IReadOnlyList<LicenceFile> licences, Action<string, string, IEnumerable<string>> add)
    {
        var limit = config.Thresholds.EvidenceLimit;
        if (licences.Count == 0)
        {
            add("licence.missing", "No licence file at the root of the repository", []);
        }

        foreach (var file in licences.Where(l => l.Spdx is null))
        {
            add("licence.unrecognised", $"{file.Path} matches none of the configured licences", [file.Path]);
        }

        foreach (var spdx in licences.Where(l => l.Spdx is not null).Select(l => l.Spdx!).Distinct(StringComparer.Ordinal))
        {
            var rule = config.Licences.First(l => l.Spdx == spdx);
            add("licence.terms", $"Licensed {spdx} ({rule.Family})", rule.Obligations);
        }

        var notice = ForkFactsReader.Pattern(config.NoticeFiles);
        var copyright = new Regex(@"^\s*(copyright\b|\(c\)|©)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var holders = facts.Files
            .Where(path => licences.Any(l => l.Path == path) || notice.IsMatch(path))
            .SelectMany(path => facts.Texts.GetValueOrDefault(path, string.Empty).Split('\n').Select(line => line.Trim()).Where(line => copyright.IsMatch(line)).Select(line => $"{path}: {line}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (holders.Count > 0)
        {
            add("licence.copyright", $"{holders.Count} copyright line(s) in the licence and notice files", holders.Take(limit));
        }

        var notices = facts.Files.Where(path => notice.IsMatch(path)).ToList();
        if (notices.Count > 0)
        {
            add("notice.carry", "Upstream ships a NOTICE file", notices);
        }

        string[] own = [.. licences.Where(l => l.Spdx is not null).Select(l => l.Spdx!)];
        var licencePaths = licences.Select(l => l.Path).Concat(notices).ToHashSet(StringComparer.Ordinal);
        var foreign = facts.HeaderHits
            .Where(hit => !licencePaths.Contains(hit.Path) && !own.Any(spdx => SameLicence(spdx, hit.Spdx)))
            .GroupBy(hit => hit.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .GroupBy(hit => hit.Spdx, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);
        foreach (var group in foreign)
        {
            var paths = group.Select(hit => hit.Path).Order(StringComparer.Ordinal).ToList();
            add("third-party.licences", $"{paths.Count} file(s) carry {group.Key} text, not the project's licence", paths.Take(limit));
        }

        var vendored = ForkFactsReader.Pattern(config.VendoredDirectories);
        var roots = facts.Files
            .Select(path => (path, match: vendored.Match(path)))
            .Where(pair => pair.match.Success)
            .Select(pair => pair.path[..(pair.match.Index + pair.match.Length)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (roots.Count > 0)
        {
            add("third-party.vendored", $"{roots.Count} vendored director(ies)", roots.Take(limit));
        }
    }

    private static void NameFindings(ForkAssessmentConfig config, ForkFacts facts, int limit, Action<string, string, IEnumerable<string>> add)
    {
        List<string> packages = [];
        foreach (var manifest in config.PackageManifests)
        {
            var path = ForkFactsReader.Pattern(manifest.Path);
            var name = new Regex(manifest.Name, RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            packages =
            [
                .. packages,
                .. facts.Texts
                    .Where(pair => path.IsMatch(pair.Key))
                    .SelectMany(pair => name.Matches(pair.Value).Select(m => m.Groups[1].Value.Trim()))
                    .Where(value => value.Length > 0 && !value.Contains("$(", StringComparison.Ordinal))
                    .Select(value => $"{manifest.Kind}: {value}"),
            ];
        }

        var distinct = packages.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (distinct.Count > 0)
        {
            add("names.packages", $"Upstream publishes {distinct.Count} package name(s)", distinct.Take(limit));
        }

        var assets = facts.Files.Where(path => ForkFactsReader.Pattern(config.BrandAssets).IsMatch(path)).ToList();
        if (assets.Count > 0)
        {
            add("names.brand-assets", $"{assets.Count} logo or icon file(s)", assets.Take(limit));
        }

        var trademarks = facts.Files.Where(path => ForkFactsReader.Pattern(config.TrademarkFiles).IsMatch(path)).ToList();
        if (trademarks.Count > 0)
        {
            add("names.trademark", "Upstream has a trademark policy", trademarks);
        }
    }

    private static void ContributionFindings(ForkAssessmentConfig config, ForkFacts facts, int limit, Action<string, string, IEnumerable<string>> add)
    {
        var policy = ForkFactsReader.Pattern(config.PolicyFiles);
        var readme = ForkFactsReader.Pattern(config.ReadmeFiles);
        var ai = MatchingLines(facts, policy, config.Markers.AiPolicy);
        if (ai.Count > 0)
        {
            add("contribution.ai-policy", $"Upstream's policy files mention AI or LLM use on {ai.Count} line(s)", ai.Take(limit));
        }

        var cla = MatchingLines(facts, policy, config.Markers.Cla);
        if (cla.Count > 0)
        {
            add("contribution.cla", "Upstream asks for a contributor licence agreement", cla.Take(limit));
        }

        var signed = facts.Commits.Count(c => c.SignedOff);
        if (signed > 0)
        {
            add("contribution.dco", $"{signed} of {facts.Commits.Count} commits are signed off", [Percent((double)signed / facts.Commits.Count)]);
        }

        var ended = MatchingLines(facts, readme, config.Markers.Unmaintained);
        if (ended.Count > 0)
        {
            add("activity.ended", "Upstream's README says it has ended or is deprecated", ended.Take(limit));
        }
    }

    private static List<string> MatchingLines(ForkFacts facts, Regex files, IReadOnlyList<string> markers)
    {
        if (markers.Count == 0)
        {
            return [];
        }

        var any = new Regex(
            $@"(?<![A-Za-z0-9])(?:{string.Join('|', markers.Select(Regex.Escape))})(?![A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return [.. facts.Texts
            .Where(pair => files.IsMatch(pair.Key))
            .SelectMany(pair => pair.Value.Split('\n').Select(line => line.Trim()).Where(line => any.IsMatch(line)).Select(line => $"{pair.Key}: {Clip(line)}"))
            .Distinct(StringComparer.Ordinal)];
    }

    private static PeopleSummary SummarisePeople(ForkAssessmentConfig config, ForkFacts facts, Identities identities)
    {
        var total = facts.Commits.Count;
        var groups = facts.Commits
            .GroupBy(identities.Of)
            .Select(group => (Key: group.Key, Commits: group.ToList()))
            .ToList();
        var keyed = groups
            .Select(g => (g.Key, Contributor: new Contributor(
                Identities.Name(g.Commits),
                Identities.Aliases(g.Commits),
                [.. g.Commits.Select(c => c.Email.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                g.Commits.Count,
                total == 0 ? 0 : (double)g.Commits.Count / total,
                g.Commits.Min(c => c.Date.Year),
                g.Commits.Max(c => c.Date.Year),
                g.Commits.Select(c => c.Date.Year).Distinct().Count())))
            .OrderByDescending(p => p.Contributor.Commits)
            .ThenBy(p => p.Contributor.Name, StringComparer.Ordinal)
            .ToList();
        var names = keyed.ToDictionary(p => p.Key, p => p.Contributor.Name, StringComparer.Ordinal);
        var contributors = keyed.Select(p => p.Contributor).ToList();

        var busFactor = 0;
        var covered = 0;
        foreach (var contributor in contributors)
        {
            if (total == 0 || covered >= config.Thresholds.BusFactorCoverage * total)
            {
                break;
            }

            covered += contributor.Commits;
            busFactor++;
        }

        var since = facts.RevisionDate.AddDays(-config.Thresholds.RecentWindowDays);
        var recent = facts.Commits.Where(c => c.Date > since).ToList();
        var recentTop = recent.Count == 0 ? 0 : (double)recent.GroupBy(identities.Of).Max(g => g.Count()) / recent.Count;

        var years = facts.Commits
            .GroupBy(c => c.Date.Year)
            .OrderBy(g => g.Key)
            .Select(g => new YearActivity(
                g.Key,
                g.Count(),
                g.Select(identities.Of).Distinct(StringComparer.Ordinal).Count(),
                [.. g.GroupBy(identities.Of)
                    .Select(p => (Name: names[p.Key], Count: p.Count()))
                    .OrderByDescending(p => p.Count).ThenBy(p => p.Name, StringComparer.Ordinal)
                    .Take(3)
                    .Select(p => $"{p.Name} ({p.Count})")]))
            .ToList();

        var identityCount = facts.Commits.Select(c => (c.Name, Email: c.Email.ToLowerInvariant())).Distinct().Count();
        return new PeopleSummary(
            total,
            facts.Merges,
            facts.Tags,
            contributors.Count,
            identityCount,
            facts.Commits.Count == 0 ? null : facts.Commits.Min(c => c.Date),
            busFactor,
            recent.Count,
            recentTop,
            [.. contributors.Take(config.Thresholds.FrequentContributors)],
            years);
    }

    private static void PeopleFindings(ForkAssessmentConfig config, ForkFacts facts, PeopleSummary people, Action<string, string, IEnumerable<string>> add)
    {
        var limit = config.Thresholds.EvidenceLimit;
        var threshold = config.Thresholds.SingleMaintainerShare;
        if (people.Frequent is [var top, ..] && top.Share >= threshold)
        {
            add(
                "people.single-maintainer",
                $"{top.Name} wrote {Percent(top.Share)} of {people.Commits} commits; {people.Contributors} people contributed, bus factor {people.BusFactor}",
                [$"{top.Name}: {top.Commits} commits, {top.FirstYear}-{top.LastYear}, as {string.Join(", ", top.Emails)}"]);
        }

        if (people.RecentCommits > 0 && people.RecentTopShare >= threshold)
        {
            add(
                "people.recent-single-maintainer",
                $"One person made {Percent(people.RecentTopShare)} of the {people.RecentCommits} commits in the {config.Thresholds.RecentWindowDays} days before {facts.RevisionDate:yyyy-MM-dd}",
                []);
        }

        var emails = facts.Commits.Select(c => c.Email.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Count();
        if (emails > 0)
        {
            add("people.personal-data", $"The history holds {emails} distinct email address(es)", people.Frequent.Where(c => c.Aliases.Count > 0 || c.Emails.Count > 1).Take(limit).Select(c => $"{c.Name} merged from {c.Emails.Count} email(s) and {c.Aliases.Count + 1} name(s)"));
        }
    }

    private static OwnershipSummary? SummariseOwnership(ForkFacts facts, Identities identities)
    {
        if (facts.Blame is null)
        {
            return null;
        }

        var total = facts.Blame.Sum(b => b.Lines);
        var leaders = facts.Blame
            .GroupBy(b => identities.Of(new CommitFact(b.Name, b.Email, default, false)))
            .Select(g => (Name: g.OrderByDescending(b => b.Lines).ThenBy(b => b.Name, StringComparer.Ordinal).First().Name, Lines: g.Sum(b => b.Lines)))
            .OrderByDescending(p => p.Lines)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new LineShare(p.Name, p.Lines, total == 0 ? 0 : (double)p.Lines / total))
            .ToList();
        return new OwnershipSummary(total, leaders);
    }

    // An SPDX id names the same licence as another when one is the other plus a suffix such as -only or -or-later.
    private static bool SameLicence(string a, string b) =>
        a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Clip(string line) => line.Length <= 160 ? line : line[..157] + "...";
}
