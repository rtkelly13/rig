using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Forks;

/// <summary>
/// Reads <see cref="ForkFacts"/> from one revision of a clone, through git alone. Files are read from the revision, not
/// the working tree, so uncommitted changes never alter the report.
/// </summary>
/// <param name="git">Runs git, hardened when the clone lies under an untrusted folder.</param>
public sealed class ForkFactsReader(Git git)
{
    private const char Record = '\u001e';
    private const char Unit = '\u001f';
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex SpdxLine = new(@"SPDX-License-Identifier:\s*([A-Za-z0-9.+-]+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The facts at <paramref name="revision"/> of the clone at <paramref name="repo"/>.</summary>
    /// <param name="repo">The clone.</param>
    /// <param name="revision">The revision to assess.</param>
    /// <param name="config">Which files to read and which markers to look for.</param>
    /// <param name="mailmap">An extra mailmap file that merges identities the repository's own does not, or null.</param>
    /// <param name="blame">Whether to attribute every line (slow on large repositories).</param>
    /// <param name="refPatterns">The refs to look for unmerged work in, as <c>git for-each-ref</c> patterns; empty means every branch.</param>
    /// <param name="cancellationToken">Stops the reads.</param>
    /// <exception cref="ProcException">The folder is not a clone, or the revision does not exist.</exception>
    public async Task<ForkFacts> ReadAsync(string repo, string revision, ForkAssessmentConfig config, string? mailmap, bool blame, IReadOnlyList<string> refPatterns, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(refPatterns);
        string[] mailmapArgs = mailmap is null ? [] : ["-c", $"mailmap.file={Path.GetFullPath(mailmap)}"];
        var sha = (await GitAsync(repo, ["rev-parse", "--verify", "--end-of-options", $"{revision}^{{commit}}"], cancellationToken).ConfigureAwait(false)).Trim();
        var date = Day((await GitAsync(repo, ["log", "-1", "--format=%cs", sha], cancellationToken).ConfigureAwait(false)).Trim());
        var shallow = (await GitAsync(repo, ["rev-parse", "--is-shallow-repository"], cancellationToken).ConfigureAwait(false)).Trim() == "true";
        var commits = Commits(await GitAsync(
            repo,
            [.. mailmapArgs, "log", "--no-merges", "--use-mailmap", $"--format={Record}%aN{Unit}%aE{Unit}%as{Unit}%(trailers:key=Signed-off-by,valueonly,separator=%x2c)", sha],
            cancellationToken).ConfigureAwait(false));
        var merges = int.Parse(await GitAsync(repo, ["rev-list", "--merges", "--count", sha], cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var tags = Lines(await GitAsync(repo, ["tag", "--merged", sha], cancellationToken).ConfigureAwait(false)).Count;

        var ignored = Pattern(config.IgnoredPaths);
        var files = Lines(await GitAsync(repo, ["-c", "core.quotePath=false", "ls-tree", "-r", "--name-only", sha], cancellationToken).ConfigureAwait(false))
            .Where(path => !ignored.IsMatch(path))
            .Order(StringComparer.Ordinal)
            .ToList();

        var texts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in files.Where(path => ToRead(config, path)))
        {
            texts[path] = await GitAsync(repo, ["show", $"{sha}:{path}"], cancellationToken).ConfigureAwait(false);
        }

        var hits = await HeaderHitsAsync(repo, sha, config, ignored, cancellationToken).ConfigureAwait(false);
        var refs = await RefsAsync(repo, sha, refPatterns.Count == 0 ? ["refs/heads", "refs/remotes"] : refPatterns, cancellationToken).ConfigureAwait(false);
        var blamed = blame ? await BlameAsync(repo, sha, Matching(files, config.BlamePaths), mailmapArgs, cancellationToken).ConfigureAwait(false) : null;
        return new ForkFacts(sha, date, shallow, commits, merges, tags, files, texts, hits, refs, blamed);
    }

    /// <summary>Whether the assessment needs <paramref name="path"/>'s text.</summary>
    public static bool ToRead(ForkAssessmentConfig config, string path)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(path);
        return Pattern(config.LicenceFiles).IsMatch(path)
            || Pattern(config.NoticeFiles).IsMatch(path)
            || Pattern(config.PolicyFiles).IsMatch(path)
            || Pattern(config.ReadmeFiles).IsMatch(path)
            || config.PackageManifests.Any(m => Pattern(m.Path).IsMatch(path));
    }

    private static List<string> Matching(IEnumerable<string> paths, string pattern)
    {
        var regex = Pattern(pattern);
        return [.. paths.Where(path => regex.IsMatch(path))];
    }

    /// <summary>A configured pattern, compiled with a timeout.</summary>
    public static Regex Pattern(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Parses <c>git log</c> output in the reader's record format.</summary>
    public static IReadOnlyList<CommitFact> Commits(string log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return [.. log.Split(Record, StringSplitOptions.RemoveEmptyEntries)
            .Select(record => record.Trim('\n', '\r').Split(Unit))
            .Where(fields => fields.Length >= 3)
            .Select(fields => new CommitFact(fields[0].Trim(), fields[1].Trim(), Day(fields[2]), fields.Length > 3 && fields[3].Trim().Length > 0))];
    }

    private static DateOnly Day(string text) => DateOnly.ParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static List<string> Lines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0)];

    private async Task<IReadOnlyList<HeaderHit>> HeaderHitsAsync(string repo, string sha, ForkAssessmentConfig config, Regex ignored, CancellationToken cancellationToken)
    {
        var prefix = $"{sha}:";
        List<HeaderHit> hits = [];
        foreach (var licence in config.Licences)
        {
            foreach (var marker in licence.HeaderMarkers)
            {
                var found = await GitAsync(repo, ["-c", "core.quotePath=false", "grep", "-I", "-l", "-i", "-F", "-e", marker, sha, "--"], cancellationToken, allowNoMatch: true).ConfigureAwait(false);
                hits = [.. hits, .. Lines(found).Select(line => line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line).Select(path => new HeaderHit(path, licence.Spdx))];
            }
        }

        var spdx = await GitAsync(repo, ["-c", "core.quotePath=false", "grep", "-I", "-H", "-o", "-E", "-e", "SPDX-License-Identifier:[[:space:]]*[A-Za-z0-9.+-]+", sha, "--"], cancellationToken, allowNoMatch: true).ConfigureAwait(false);
        foreach (var line in Lines(spdx))
        {
            var rest = line.StartsWith(prefix, StringComparison.Ordinal) ? line[prefix.Length..] : line;
            var match = SpdxLine.Match(rest);
            var colon = rest.IndexOf(":SPDX-License-Identifier", StringComparison.Ordinal);
            if (match.Success && colon > 0)
            {
                hits = [.. hits, new HeaderHit(rest[..colon], match.Groups[1].Value)];
            }
        }

        return [.. hits.Where(hit => !ignored.IsMatch(hit.Path)).Distinct().OrderBy(hit => hit.Path, StringComparer.Ordinal).ThenBy(hit => hit.Spdx, StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyList<RefFact>> RefsAsync(string repo, string sha, IReadOnlyList<string> patterns, CancellationToken cancellationToken)
    {
        // Symbolic refs (origin/HEAD) only repeat another ref, so they are left out.
        var listed = Lines(await GitAsync(repo, ["for-each-ref", "--format=%(refname)%09%(refname:short)%09%(symref)", .. patterns], cancellationToken).ConfigureAwait(false))
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 2 && (parts.Length < 3 || parts[2].Length == 0))
            .Select(parts => (Full: parts[0], Short: parts[1]))
            .OrderBy(r => r.Full, StringComparer.Ordinal)
            .ToList();
        List<RefFact> refs = [];
        foreach (var (full, shortName) in listed)
        {
            var unique = int.Parse(await GitAsync(repo, ["rev-list", "--no-merges", "--count", full, $"^{sha}"], cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (unique > 0)
            {
                var last = Day(await GitAsync(repo, ["log", "-1", "--format=%cs", full], cancellationToken).ConfigureAwait(false));
                refs.Add(new RefFact(shortName, unique, last));
            }
        }

        return refs;
    }

    private async Task<IReadOnlyList<BlameFact>> BlameAsync(string repo, string sha, IReadOnlyList<string> paths, IReadOnlyList<string> mailmapArgs, CancellationToken cancellationToken)
    {
        var counts = new ConcurrentDictionary<(string Name, string Email), int>();
        await Parallel.ForEachAsync(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, token) =>
            {
                var porcelain = await GitAsync(repo, [.. mailmapArgs, "blame", "-w", "-M", "--line-porcelain", sha, "--", path], token).ConfigureAwait(false);
                string? name = null;
                foreach (var line in porcelain.Split('\n'))
                {
                    if (line.StartsWith("author ", StringComparison.Ordinal))
                    {
                        name = line["author ".Length..].Trim();
                    }
                    else if (line.StartsWith("author-mail ", StringComparison.Ordinal) && name is not null)
                    {
                        var email = line["author-mail ".Length..].Trim().Trim('<', '>');
                        _ = counts.AddOrUpdate((name, email), 1, (_, n) => n + 1);
                        name = null;
                    }
                }
            }).ConfigureAwait(false);
        return [.. counts.Select(pair => new BlameFact(pair.Key.Name, pair.Key.Email, pair.Value))
            .OrderBy(b => b.Name, StringComparer.Ordinal).ThenBy(b => b.Email, StringComparer.Ordinal)];
    }

    private async Task<string> GitAsync(string repo, IReadOnlyList<string> args, CancellationToken cancellationToken, bool allowNoMatch = false)
    {
        var ran = await git.RunAsync(repo, args, LongTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        // git grep exits 1 when nothing matched, which is an answer, not a failure.
        return allowNoMatch && ran.Code == 1 && ran.Err.Trim().Length == 0 ? string.Empty : ran.EnsureOk(["git", .. args]).Out;
    }
}
