using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Governance;

/// <summary>How a caller should treat an AGENTS.md audit finding.</summary>
public enum AgentsMdSeverity
{
    /// <summary>Fails the audit.</summary>
    Error,

    /// <summary>Fails only a strict audit.</summary>
    Warn,

    /// <summary>Reported, never failing.</summary>
    Info,
}

/// <summary>One thing the audit noticed, addressable by a waiver.</summary>
/// <param name="Check">The check's name, such as <c>pointer-integrity</c>.</param>
/// <param name="Severity">How it is treated.</param>
/// <param name="Path">The repo-relative file it is about, with forward slashes.</param>
/// <param name="Message">What is wrong and what to do.</param>
/// <param name="Line">The 1-based line it is on, when it is about one line.</param>
public sealed record AgentsMdFinding(string Check, AgentsMdSeverity Severity, string Path, string Message, int? Line = null)
{
    /// <summary>The stable identity a waiver names: deliberately not the message, whose wording may change.</summary>
    public string Key => $"{Check}:{Path}";

    /// <summary>The finding as the <c>--json</c> report prints it.</summary>
    public JsonObject ToJson() => new()
    {
        ["check"] = Check,
        ["severity"] = AgentsMdAudit.SeverityName(Severity),
        ["path"] = Path,
        ["message"] = Message,
        ["line"] = Line,
    };
}

/// <summary>A committed dismissal of one finding.</summary>
/// <param name="Check">The check it dismisses.</param>
/// <param name="Path">The file it dismisses the check for.</param>
/// <param name="Reason">Why; a waiver without one does not suppress.</param>
/// <param name="Expires">An ISO date after which it stops suppressing, or null.</param>
public sealed record AgentsMdWaiver(string Check, string Path, string Reason = "", string? Expires = null)
{
    /// <summary>The finding key it suppresses.</summary>
    public string Key => $"{Check}:{Path}";

    /// <summary>Whether it has lapsed by <paramref name="today"/>. A date that cannot be read has lapsed.</summary>
    public bool ExpiredOn(DateOnly today) =>
        !string.IsNullOrEmpty(Expires) && (AgentsMdAudit.ParseIsoDate(Expires) is not { } date || date < today);

    /// <summary>The waiver as <c>.agents-audit.json</c> holds it.</summary>
    public JsonObject ToJson() => new() { ["check"] = Check, ["path"] = Path, ["reason"] = Reason, ["expires"] = Expires };
}

/// <summary>Everything one repo's audit produced.</summary>
/// <param name="Repo">The repo's folder name.</param>
/// <param name="Findings">What survived the waivers.</param>
/// <param name="Suppressed">What a waiver dismissed.</param>
/// <param name="Metrics">The report-only numbers, in report order.</param>
public sealed record AgentsMdAuditResult(
    string Repo,
    IReadOnlyList<AgentsMdFinding> Findings,
    IReadOnlyList<AgentsMdFinding> Suppressed,
    IReadOnlyList<KeyValuePair<string, int>> Metrics)
{
    /// <summary>True when a surviving finding is an error.</summary>
    public bool Failed => Findings.Any(finding => finding.Severity == AgentsMdSeverity.Error);

    /// <summary>The result as the <c>--json</c> report prints it.</summary>
    public JsonObject ToJson()
    {
        var metrics = new JsonObject();
        foreach (var (name, value) in Metrics)
        {
            metrics[name] = value;
        }

        return new()
        {
            ["repo"] = Repo,
            ["findings"] = new JsonArray([.. Findings.Select(JsonNode (f) => f.ToJson())]),
            ["suppressed"] = new JsonArray([.. Suppressed.Select(JsonNode (f) => f.ToJson())]),
            ["metrics"] = metrics,
        };
    }
}

/// <summary>
/// Audits <c>AGENTS.md</c> and the docs tree against the machine-decidable rules of
/// <c>docs/agents/agent-consistency.md</c>. Every finding carries a stable <c>check:path</c> key a repo may dismiss by
/// committing a waiver to <c>.agents-audit.json</c>; waivers need a reason, may expire, and are reported once they
/// suppress nothing. Ported from <c>rig.repo_governance.agents_md_audit</c>.
/// </summary>
/// <param name="git">Runs git, to read whether <c>CLAUDE.md</c> is committed as a link.</param>
/// <param name="time">The clock waivers expire by.</param>
public sealed partial class AgentsMdAudit(Git git, TimeProvider time)
{
    /// <summary>The waiver file at a repo's root.</summary>
    public const string WaiverFileName = ".agents-audit.json";

    /// <summary>The root instruction file.</summary>
    public const string AgentsFileName = "AGENTS.md";

    /// <summary>Checks that fail an audit.</summary>
    public static readonly IReadOnlyList<string> GatingChecks =
    [
        "root-size", "duplicate-heading", "pointer-integrity", "docs-orphan", "claude-md-shape", "bespoke-agent-file",
        "path-liveness", "required-content", "generation-marker", "docs-index", "folder-index", "docs-code-block",
    ];

    /// <summary>Checks that are reported as metrics and never fail.</summary>
    public static readonly IReadOnlyList<string> ReportOnlyChecks = ["instruction-count", "forcing-language", "section-outlier"];

    private const string GitSymlinkMode = "120000";
    private static readonly string[] PackageManagers = ["pnpm", "npm", "yarn", "bun", "uv", "poetry", "pip", "cargo", "dotnet", "go "];
    private static readonly string[] GenerationMarkers = ["<!-- generated", "auto-generated", "do not edit — generated", "generated by"];
    private static readonly string[] IndexNames = ["README.md", "index.md"];
    private static readonly string[] DescriptionPrefixes = ["#", "-", "|", "`", ">"];

    /// <summary>A severity as the JSON report names it.</summary>
    public static string SeverityName(AgentsMdSeverity severity) => severity switch
    {
        AgentsMdSeverity.Error => "error",
        AgentsMdSeverity.Warn => "warn",
        _ => "info",
    };

    /// <summary>An ISO calendar date (<c>YYYY-MM-DD</c> or <c>YYYYMMDD</c>), or null when it is not one.</summary>
    public static DateOnly? ParseIsoDate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = IsoDate().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        return year >= 1 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    /// <summary>
    /// The waivers in <paramref name="repoPath"/>'s <c>.agents-audit.json</c>; empty when it is absent or does not parse,
    /// so one bad edit cannot take the audit down.
    /// </summary>
    public static IReadOnlyList<AgentsMdWaiver> LoadWaivers(string repoPath)
    {
        var path = Path.Join(repoPath, WaiverFileName);
        if (!DocsTree.IsFile(path))
        {
            return [];
        }

        try
        {
            return ParseWaivers(JsonNode.Parse(DocsTree.ReadText(path))) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The waiver file holding <paramref name="waivers"/>, as written back: indented JSON and a newline.</summary>
    public static string RenderWaivers(IEnumerable<AgentsMdWaiver> waivers) =>
        Policy.PyJson.Dumps(new JsonObject { ["waivers"] = new JsonArray([.. waivers.Select(JsonNode (w) => w.ToJson())]) }) + "\n";

    /// <summary>Runs every deterministic check over <paramref name="repoPath"/> under <paramref name="rules"/> and applies its waivers.</summary>
    public async Task<AgentsMdAuditResult> AuditAsync(string repoPath, AgentsMdRules rules, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var repo = PathResolution.Resolve(repoPath);
        var name = Path.GetFileName(repo.TrimEnd(Path.DirectorySeparatorChar));
        var agents = Path.Join(repo, AgentsFileName);
        if (!DocsTree.IsFile(agents))
        {
            return new(name, [new("required-content", AgentsMdSeverity.Error, AgentsFileName, "No AGENTS.md. Every managed repo needs one, however short.")], [], []);
        }

        var text = DocsTree.ReadText(agents);
        var docs = MarkdownFiles(repo);
        List<AgentsMdFinding> raw =
        [
            .. CheckRootSize(text, rules),
            .. CheckDuplicateHeadings(text),
            .. CheckPointers(repo, docs),
            .. CheckDocsOrphans(repo, docs),
            .. CheckDocsIndex(repo, rules),
            .. CheckFolderIndex(repo),
            .. CheckCodeBlocks(repo, docs, rules),
            .. await CheckClaudeMdAsync(repo, rules, cancellationToken).ConfigureAwait(false),
            .. CheckBespokeAgentFiles(repo, rules),
            .. CheckRequiredContent(text, rules),
            .. CheckGenerationMarkers(text),
        ];
        if (rules.CheckPaths)
        {
            raw.AddRange(CheckPathLiveness(repo, text));
        }

        var waivers = LoadWaivers(repo);
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        var active = waivers.Where(w => PyStrip(w.Reason).Length > 0 && !w.ExpiredOn(today)).Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
        var findings = raw.Where(f => !active.Contains(f.Key)).ToList();
        var suppressed = raw.Where(f => active.Contains(f.Key)).ToList();
        findings.AddRange(WaiverFindings(repo, waivers, raw.Select(f => f.Key).ToHashSet(StringComparer.Ordinal), today));
        return new(name, findings, suppressed, CollectMetrics(text));
    }

    /// <summary>The report-only numbers: list items, forcing words, and the longest run of lines under one heading.</summary>
    public static IReadOnlyList<KeyValuePair<string, int>> CollectMetrics(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text);
        var sections = new List<int>();
        var current = 0;
        foreach (var line in lines)
        {
            if (Heading().IsMatch(line))
            {
                sections.Add(current);
                current = 0;
            }
            else
            {
                current++;
            }
        }

        sections.Add(current);
        return
        [
            new("instruction-count", lines.Count(line => Instruction().IsMatch(line))),
            new("forcing-language", Forcing().Count(text)),
            new("section-outlier", sections.Max()),
        ];
    }

    /// <summary>The text with fenced blocks and inline code spans blanked: a link in code is an example, not a pointer.</summary>
    public static string StripCode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var kept = new List<string>();
        var inFence = false;
        foreach (var line in SplitLines(text))
        {
            if (Fence().IsMatch(line))
            {
                inFence = !inFence;
                kept.Add(string.Empty);
                continue;
            }

            kept.Add(inFence ? string.Empty : CodeSpan().Replace(line, string.Empty));
        }

        return string.Join('\n', kept);
    }

    /// <summary>Python's <c>str.splitlines()</c>.</summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    private static IEnumerable<AgentsMdFinding> CheckRootSize(string text, AgentsMdRules rules)
    {
        var lines = text.Count(c => c == '\n') + 1;
        if (lines > rules.MaxLines)
        {
            yield return new("root-size", AgentsMdSeverity.Error, AgentsFileName,
                $"{lines} lines against a budget of {rules.MaxLines}. Move domain rules into `docs/` and point at them.");
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckDuplicateHeadings(string text)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var number = 0;
        foreach (var line in SplitLines(text))
        {
            number++;
            var match = Heading().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var title = PyStrip(match.Groups[2].Value);
            if (seen.TryGetValue(title, out var first))
            {
                yield return new("duplicate-heading", AgentsMdSeverity.Error, AgentsFileName,
                    $"`{title}` already appears at line {first}. Two copies contradict each other.", number);
            }
            else
            {
                seen[title] = number;
            }
        }
    }

    // Link targets that point at a path in this repo, percent-decoded as the file on disk is named.
    private static IEnumerable<string> RelativeTargets(string text)
    {
        foreach (Match match in MdLink().Matches(StripCode(text)))
        {
            var target = PyStrip(match.Groups[1].Value.Split('#', 2)[0]);
            if (target.Length == 0 || target.StartsWith("http://", StringComparison.Ordinal) || target.StartsWith("https://", StringComparison.Ordinal)
                || target.StartsWith("mailto:", StringComparison.Ordinal) || target.StartsWith('#'))
            {
                continue;
            }

            yield return Uri.UnescapeDataString(target);
        }
    }

    private static string? TryRead(string path)
    {
        try
        {
            return DocsTree.ReadText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckPointers(string repo, IReadOnlyList<string> docs)
    {
        foreach (var doc in docs)
        {
            if (TryRead(doc) is not { } text)
            {
                continue;
            }

            var rel = DocsTree.Relative(repo, doc);
            foreach (var target in RelativeTargets(text))
            {
                if (DocsTree.Resolve(Parent(doc), target) is not { } resolved || !DocsTree.Exists(resolved))
                {
                    yield return new("pointer-integrity", AgentsMdSeverity.Error, rel, $"Link target `{target}` does not exist.");
                }
            }
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckDocsOrphans(string repo, IReadOnlyList<string> docs)
    {
        var linked = new HashSet<string>(DocsTree.Comparer);
        foreach (var doc in docs)
        {
            if (TryRead(doc) is not { } text)
            {
                continue;
            }

            foreach (var target in RelativeTargets(text))
            {
                if (DocsTree.Resolve(Parent(doc), target) is { } resolved && DocsTree.Exists(resolved))
                {
                    linked.Add(resolved);
                }
            }
        }

        foreach (var doc in docs)
        {
            if (Path.GetFileName(doc) == AgentsFileName || linked.Contains(PathResolution.Resolve(doc)))
            {
                continue;
            }

            // A folder index that fails to link this doc is reported by `folder-index` instead.
            if (IsIndex(doc) || FolderIndex(Parent(doc)) is not null)
            {
                continue;
            }

            yield return new("docs-orphan", AgentsMdSeverity.Warn, DocsTree.Relative(repo, doc),
                "Nothing links here, so no agent will load it. Point at it or delete it.");
        }
    }

    private async Task<IReadOnlyList<AgentsMdFinding>> CheckClaudeMdAsync(string repo, AgentsMdRules rules, CancellationToken cancellationToken)
    {
        if (rules.ClaudeMd == "any")
        {
            return [];
        }

        var path = Path.Join(repo, "CLAUDE.md");
        var exists = DocsTree.IsLink(path) || DocsTree.Exists(path);
        if (rules.ClaudeMd == "absent" && exists)
        {
            return [new("claude-md-shape", AgentsMdSeverity.Warn, "CLAUDE.md", "Policy is `absent`, but this file exists. Agents read AGENTS.md directly.")];
        }

        if (rules.ClaudeMd == "symlink" && exists && !await IsSymlinkOrPlaceholderAsync(repo, path, cancellationToken).ConfigureAwait(false))
        {
            return [new("claude-md-shape", AgentsMdSeverity.Warn, "CLAUDE.md", "A copy, not a symlink — so it drifts from AGENTS.md. Run `ln -sf AGENTS.md CLAUDE.md`.")];
        }

        return [];
    }

    // A symlink, or the plain file git checks out in place of a committed symlink where links are unsupported:
    // the index still records the link mode, and the file holds just the target.
    private async Task<bool> IsSymlinkOrPlaceholderAsync(string repo, string path, CancellationToken cancellationToken)
    {
        if (DocsTree.IsLink(path))
        {
            return true;
        }

        var listed = await git.RunAsync(repo, ["ls-files", "--stage", "--", "CLAUDE.md"], TimeSpan.FromSeconds(10), cancellationToken: cancellationToken).ConfigureAwait(false);
        var fields = listed.Out.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        if (!listed.Ok || fields.Length == 0 || fields[0] != GitSymlinkMode)
        {
            return false;
        }

        try
        {
            return PyStrip(DocsTree.ReadText(path)) == AgentsFileName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckBespokeAgentFiles(string repo, AgentsMdRules rules)
    {
        foreach (var relative in rules.BespokeAgentFiles)
        {
            var name = relative.TrimEnd('/');
            var path = Path.Join(repo, name);
            if (DocsTree.IsLink(path) || DocsTree.Exists(path))
            {
                yield return new("bespoke-agent-file", AgentsMdSeverity.Error, name,
                    "Merge anything worth keeping into AGENTS.md, then delete this. The tool that owns this name reads it instead of AGENTS.md.");
            }
        }
    }

    // Backticked text plausibly naming a repo path: it needs a separator, and a dot in its last segment.
    private static bool LooksLikePath(string candidate)
    {
        if (!candidate.Contains('/', StringComparison.Ordinal)
            || candidate.StartsWith('/') || candidate.StartsWith('~') || candidate.StartsWith("http", StringComparison.Ordinal)
            || candidate.StartsWith('@') || candidate.StartsWith('-'))
        {
            return false;
        }

        return candidate.IndexOfAny([' ', '*', '<', '>', '|', '$', '(', ')', '{', '}']) < 0
            && candidate[(candidate.LastIndexOf('/') + 1)..].Contains('.', StringComparison.Ordinal);
    }

    private static IEnumerable<AgentsMdFinding> CheckPathLiveness(string repo, string text)
    {
        var number = 0;
        foreach (var line in SplitLines(text))
        {
            number++;
            foreach (Match match in Backticked().Matches(line))
            {
                var candidate = PyStrip(match.Groups[1].Value);
                if (LooksLikePath(candidate) && !DocsTree.Exists(Path.Combine(repo, candidate)))
                {
                    yield return new("path-liveness", AgentsMdSeverity.Warn, AgentsFileName,
                        $"`{candidate}` does not exist. Describe the capability, not the location.", number);
                }
            }
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckRequiredContent(string text, AgentsMdRules rules)
    {
        var lowered = text.ToLowerInvariant();
        var head = SplitLines(string.Join('\n', SplitLines(text).Take(12)));
        var present = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["description"] = head.Any(line => PyStrip(line).Length > 0 && !DescriptionPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))),
            ["package_manager"] = PackageManagers.Any(manager => lowered.Contains(manager, StringComparison.Ordinal)),
            ["commands"] = lowered.Contains("## commands", StringComparison.Ordinal) || text.Contains("```", StringComparison.Ordinal),
        };
        foreach (var name in rules.Require)
        {
            if (!present.GetValueOrDefault(name, true))
            {
                yield return new("required-content", AgentsMdSeverity.Error, AgentsFileName,
                    $"No {name.Replace('_', ' ')} found. An agent cannot orient without it.");
            }
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckGenerationMarkers(string text)
    {
        var lowered = text.ToLowerInvariant();
        if (GenerationMarkers.FirstOrDefault(marker => lowered.Contains(marker, StringComparison.Ordinal)) is { } marker)
        {
            yield return new("generation-marker", AgentsMdSeverity.Error, AgentsFileName,
                $"Looks auto-generated (`{marker}`). Write it by hand; generators do not exercise restraint.");
        }
    }

    private static string? FolderIndex(string folder) =>
        IndexNames.Select(name => Path.Join(folder, name)).FirstOrDefault(DocsTree.IsFile);

    // Exact names, as FolderIndex finds them: on a case-sensitive file system `Index.md` is a page.
    private static bool IsIndex(string path) => IndexNames.Contains(Path.GetFileName(path), StringComparer.Ordinal);

    // An index's resolved link targets. A link to `dir/README.md` also counts as `dir`, and a folder with no index
    // of its own is a pass-through, so a link to anything beneath it counts for it too.
    private static HashSet<string> LinkedFrom(string index)
    {
        var linked = new HashSet<string>(DocsTree.Comparer);
        if (TryRead(index) is not { } text)
        {
            return linked;
        }

        var folder = Parent(index);
        var bas = PathResolution.Resolve(folder);
        foreach (var target in RelativeTargets(text))
        {
            if (DocsTree.Resolve(folder, target) is not { } resolved)
            {
                continue;
            }

            linked.Add(resolved);
            var start = Parent(resolved);
            if (IsIndex(resolved))
            {
                linked.Add(start);
                start = Parent(start);
            }

            foreach (var ancestor in DocsTree.SelfAndParents(start))
            {
                if (DocsTree.Same(ancestor, bas) || !DocsTree.IsRelativeTo(ancestor, bas) || FolderIndex(ancestor) is not null)
                {
                    break;
                }

                linked.Add(ancestor);
            }
        }

        return linked;
    }

    // What a folder's index should link: its other markdown files and the subfolders holding any.
    private static List<string> IndexEntries(string folder)
    {
        var listed = DocsTree.List(folder).ToList();
        var entries = listed.Where(p => DocsTree.IsMarkdownName(Path.GetFileName(p)) && DocsTree.IsFile(p) && !IsIndex(p)).ToList();
        entries.AddRange(listed.Where(d => DocsTree.IsDir(d) && DocsTree.Walk(d).Any(p => DocsTree.IsMarkdownName(Path.GetFileName(p)))));
        entries.Sort(DocsTree.Order);
        return entries;
    }

    private static IEnumerable<AgentsMdFinding> CheckDocsIndex(string repo, AgentsMdRules rules)
    {
        var docsDir = Path.Join(repo, "docs");
        if (!DocsTree.IsDir(docsDir) || DocsTree.Walk(docsDir).Count(p => DocsTree.IsMarkdownName(Path.GetFileName(p))) < rules.DocsIndexMinFiles)
        {
            yield break;
        }

        if (FolderIndex(docsDir) is not { } index)
        {
            yield return new("docs-index", AgentsMdSeverity.Warn, "docs/README.md", "No docs index. Add docs/README.md linking each top-level doc and folder.");
            yield break;
        }

        var linked = LinkedFrom(index);
        foreach (var entry in IndexEntries(docsDir))
        {
            if (!linked.Contains(PathResolution.Resolve(entry)))
            {
                yield return new("docs-index", AgentsMdSeverity.Warn, DocsTree.Relative(repo, entry),
                    $"Not linked from docs/{Path.GetFileName(index)}, so an agent reading the index won't find it.");
            }
        }
    }

    private static IEnumerable<AgentsMdFinding> CheckFolderIndex(string repo)
    {
        var docsDir = Path.Join(repo, "docs");
        if (!DocsTree.IsDir(docsDir))
        {
            yield break;
        }

        var folders = DocsTree.Walk(docsDir)
            .Where(d => DocsTree.IsDir(d) && DocsTree.List(d).Any(p => DocsTree.IsMarkdownName(Path.GetFileName(p))))
            .Order(DocsTree.Order)
            .ToList();
        foreach (var folder in folders)
        {
            var rel = DocsTree.Relative(repo, folder);
            var linked = new HashSet<string>(DocsTree.Comparer);
            foreach (var level in DocsTree.SelfAndParents(folder).TakeWhile(level => DocsTree.IsRelativeTo(level, docsDir)))
            {
                if (FolderIndex(level) is { } levelIndex)
                {
                    linked.UnionWith(LinkedFrom(levelIndex));
                }
            }

            var missing = IndexEntries(folder).Where(entry => !linked.Contains(PathResolution.Resolve(entry))).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            if (FolderIndex(folder) is not { } own)
            {
                yield return new("folder-index", AgentsMdSeverity.Warn, $"{rel}/README.md",
                    "This folder has no README.md to say what is in it and when to read each part.");
                continue;
            }

            foreach (var entry in missing)
            {
                yield return new("folder-index", AgentsMdSeverity.Warn, DocsTree.Relative(repo, entry), $"Not linked from {rel}/{Path.GetFileName(own)}.");
            }
        }
    }

    // Fenced blocks in docs/ longer than the budget: a copy of source, config or output goes stale. Diagrams are drawn.
    private static IEnumerable<AgentsMdFinding> CheckCodeBlocks(string repo, IReadOnlyList<string> docs, AgentsMdRules rules)
    {
        foreach (var doc in docs)
        {
            var rel = DocsTree.Relative(repo, doc);
            if (!rel.StartsWith("docs/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = SplitLines(DocsTree.ReadText(doc));
            var i = 0;
            while (i < lines.Count)
            {
                var match = FenceOpen().Match(lines[i]);
                if (!match.Success)
                {
                    i++;
                    continue;
                }

                var fence = match.Groups[1].Value;
                var language = match.Groups[2].Value.ToLowerInvariant();
                var end = i + 1;
                while (end < lines.Count && !PyStrip(lines[end]).StartsWith(fence, StringComparison.Ordinal))
                {
                    end++;
                }

                var length = end - i - 1;
                if (length > rules.MaxCodeBlockLines && language != "mermaid")
                {
                    yield return new("docs-code-block", AgentsMdSeverity.Warn, rel,
                        $"{length}-line code block at line {i + 1}. Link the source, config or `--help` instead of copying it.");
                }

                i = end + 1;
            }
        }
    }

    // AGENTS.md, then every markdown file under docs/, sorted.
    private static List<string> MarkdownFiles(string repo)
    {
        var found = new List<string>();
        var root = Path.Join(repo, AgentsFileName);
        if (DocsTree.IsFile(root))
        {
            found.Add(root);
        }

        var docsDir = Path.Join(repo, "docs");
        if (DocsTree.IsDir(docsDir))
        {
            found.AddRange(DocsTree.Walk(docsDir).Where(p => DocsTree.IsMarkdownName(Path.GetFileName(p)) && DocsTree.IsFile(p)).Order(DocsTree.Order));
        }

        return found;
    }

    private static List<AgentsMdFinding> WaiverFindings(string repo, IReadOnlyList<AgentsMdWaiver> waivers, HashSet<string> keysSeen, DateOnly today)
    {
        var findings = new List<AgentsMdFinding>();
        if (!DocsTree.IsFile(Path.Join(repo, WaiverFileName)))
        {
            return findings;
        }

        foreach (var waiver in waivers)
        {
            if (PyStrip(waiver.Reason).Length == 0)
            {
                findings.Add(new("waiver-unreasoned", AgentsMdSeverity.Error, WaiverFileName,
                    $"Waiver for `{waiver.Key}` has no reason. A dismissal without a reason is not reviewable."));
            }

            if (waiver.ExpiredOn(today))
            {
                findings.Add(new("waiver-expired", AgentsMdSeverity.Warn, WaiverFileName,
                    $"Waiver for `{waiver.Key}` expired on {waiver.Expires} and no longer suppresses."));
            }
            else if (!keysSeen.Contains(waiver.Key))
            {
                findings.Add(new("waiver-stale", AgentsMdSeverity.Info, WaiverFileName,
                    $"Waiver for `{waiver.Key}` suppresses nothing — the finding is fixed. Remove it."));
            }
        }

        return findings;
    }

    // Each waiver as pydantic validates it: strings must be strings, and one bad waiver rejects the file.
    private static List<AgentsMdWaiver>? ParseWaivers(JsonNode? root)
    {
        if (root is not JsonObject file)
        {
            return null;
        }

        if (!file.TryGetPropertyValue("waivers", out var listed))
        {
            return [];
        }

        if (listed is not JsonArray array)
        {
            return null;
        }

        var waivers = new List<AgentsMdWaiver>();
        foreach (var item in array)
        {
            if (item is not JsonObject waiver
                || Text(waiver, "check", required: true) is not { } check
                || Text(waiver, "path", required: true) is not { } path
                || Text(waiver, "reason", required: false) is not { } reason)
            {
                return null;
            }

            string? expires = null;
            if (waiver.TryGetPropertyValue("expires", out var value) && value is not null)
            {
                if (value.GetValueKind() != System.Text.Json.JsonValueKind.String)
                {
                    return null;
                }

                expires = value.GetValue<string>();
            }

            waivers.Add(new(check, path, reason, expires));
        }

        return waivers;
    }

    // A string field: its value, "" when absent and optional, or null when it is required and absent or not a string.
    private static string? Text(JsonObject node, string name, bool required)
    {
        if (!node.TryGetPropertyValue(name, out var value))
        {
            return required ? null : string.Empty;
        }

        return value is not null && value.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private static string Parent(string path) => Path.GetDirectoryName(path) ?? path;

    // Python's str.strip(): its whitespace also takes in the separators \x1c to \x1f.
    private static string PyStrip(string text)
    {
        static bool Space(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';
        var start = 0;
        var end = text.Length;
        while (start < end && Space(text[start]))
        {
            start++;
        }

        while (end > start && Space(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    [GeneratedRegex(@"\b(MUST|NEVER|ALWAYS|REQUIRED)\b")]
    private static partial Regex Forcing();

    [GeneratedRegex(@"\[[^\]]*\]\(([^)]+)\)")]
    private static partial Regex MdLink();

    [GeneratedRegex(@"^(#{2,3})\s+(.*?)\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"`([^`\n]+)`")]
    private static partial Regex Backticked();

    [GeneratedRegex(@"^\s*(?:\d+\.|[-*])\s+\S")]
    private static partial Regex Instruction();

    [GeneratedRegex(@"^\s*(?:```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"`+[^`\n]+`+")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^\s*(`{3,}|~{3,})\s*([\w+-]*)")]
    private static partial Regex FenceOpen();

    [GeneratedRegex(@"^(?:(?<y>[0-9]{4})-(?<m>[0-9]{2})-(?<d>[0-9]{2})|(?<y>[0-9]{4})(?<m>[0-9]{2})(?<d>[0-9]{2}))\z")]
    private static partial Regex IsoDate();
}
