using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>A metadata finding on one note.</summary>
/// <param name="Note">The note.</param>
/// <param name="Code">A <c>TVS0xx</c> or <c>TVS201</c> <see cref="LintCodes"/> code.</param>
/// <param name="Reason">What is wrong.</param>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
public sealed record MetadataFailure(VaultEntry Note, string Code, string Reason, string Severity);

/// <summary>A filename finding on one note, with the name the cleanup would give it.</summary>
/// <param name="Note">The note.</param>
/// <param name="Code">A <c>TVS1xx</c> <see cref="LintCodes"/> code.</param>
/// <param name="Reason">What is wrong.</param>
/// <param name="Severity">Always <c>warning</c> today.</param>
/// <param name="SuggestedStem">The stem the cleanup would rename the note to.</param>
/// <param name="SuggestedPath">The note's path after that rename, relative to the vault.</param>
public sealed record FilenameIssue(VaultEntry Note, string Code, string Reason, string Severity, string SuggestedStem, NotePath SuggestedPath);

/// <summary>Notes that duplicate one another, under the normalized URL or <c>title:</c> key they share.</summary>
/// <param name="Key">The shared key.</param>
/// <param name="Notes">The duplicates, in path order (resources before social posts once genuine links are filtered).</param>
public sealed record DuplicateGroup(string Key, IReadOnlyList<VaultEntry> Notes);

/// <summary>
/// The vault lint, ported from <c>rig.obsidian.lint_manager.VaultLinter</c> and <c>lint_duplicates</c>: required metadata
/// per note type, filenames the cleanup would rename, and notes that duplicate one another by source URL or title. Every
/// note is read once. Findings match the Python's; where the Python visited notes in directory order, this visits them in
/// code-point order.
/// </summary>
public sealed partial class VaultLinter
{
    private const string Error = "error";
    private const string Warning = "warning";

    private static readonly string[] LintRoots = ["Resources", "Social Media Posts", "Areas", "Projects"];
    private static readonly string[] HubStems = ["Resources", "Areas", "Projects", "Archive"];
    private static readonly string[] HubRoots = ["Resources", "Areas", "Projects"];
    private static readonly string[] SourceOptional = ["resource-hub", "area", "project"];

    /// <summary>The fields each note type requires. A type missing here is reported as unknown.</summary>
    public static IReadOnlyDictionary<string, string[]> TypeRequirements { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["resource"] = ["source", "status", "created"],
        ["resource-hub"] = ["status"],
        ["social"] = ["source", "related_article", "status", "created"],
        ["project"] = ["status", "created"],
        ["project-note"] = ["status", "created"],
        ["design-note"] = ["status", "created"],
        ["area"] = ["status", "created"],
        ["daily"] = ["created"],
        ["note"] = [],
        ["task"] = ["status"],
        ["spec"] = ["status"],
        ["Repository"] = [],
        ["Reference"] = [],
    };

    private readonly string vaultName;
    private readonly Action<string>? log;
    private readonly Dictionary<string, NoteRead> reads = new(StringComparer.Ordinal);
    private IReadOnlyList<VaultEntry>? entries;
    private IReadOnlyList<DuplicateGroup>? duplicates;

    /// <summary>A linter over the vault at <paramref name="vaultPath"/>.</summary>
    /// <param name="vaultPath">The vault folder.</param>
    /// <param name="log">Told about each note a check had to pass over, as the Python logged it.</param>
    public VaultLinter(string vaultPath, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);
        VaultPath = vaultPath;
        vaultName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(vaultPath)));
        this.log = log;
    }

    /// <summary>The vault folder, as given.</summary>
    public string VaultPath { get; }

    private IReadOnlyList<VaultEntry> Entries => entries ??= VaultWalk.Markdown(VaultPath);

    /// <summary>Notes missing the metadata their type requires, with unreadable frontmatter, or of an unknown type.</summary>
    public IReadOnlyList<MetadataFailure> FindMetadataFailures()
    {
        var failures = new List<MetadataFailure>();
        foreach (var note in Entries.Where(ShouldLint))
        {
            var read = Read(note);
            if (read.Front.Error is { } error)
            {
                failures.Add(new(note, LintCodes.InvalidFrontmatter, $"Invalid frontmatter: {error}", Error));
                continue;
            }

            var ignored = read.Ignored;
            var type = read.Front.Text("type");
            if (string.IsNullOrEmpty(type))
            {
                if (!IsIgnored(ignored, LintCodes.MissingType))
                {
                    var suggested = LintRules.SuggestedType(note.Relative) ?? "None";
                    failures.Add(new(note, LintCodes.MissingType, $"Missing 'type' field (Suggested: '{suggested}')", Error));
                }

                continue;
            }

            if (!TypeRequirements.TryGetValue(type, out var required))
            {
                if (!IsIgnored(ignored, LintCodes.UnknownType))
                {
                    failures.Add(new(note, LintCodes.UnknownType, $"Unknown note type: '{type}'", Warning));
                }

                continue;
            }

            foreach (var field in required)
            {
                var (code, reason) = read.Front.Get(field) switch
                {
                    null or PyNone => (LintCodes.MissingRequiredField, $"Type '{type}' missing required field: '{field}'"),
                    PyStr { Text: var text } when PyText.Strip(text).Length == 0 => (LintCodes.EmptyRequiredField, $"Type '{type}' has empty field: '{field}'"),
                    PyList { Items.Count: 0 } => (LintCodes.EmptyRequiredList, $"Type '{type}' has empty list: '{field}'"),
                    _ => (null, null),
                };
                if (code is null || reason is null || (field == "source" && SourceOptional.Contains(type)) || IsIgnored(ignored, code))
                {
                    continue;
                }

                failures.Add(new(note, code, reason, Error));
            }
        }

        return failures;
    }

    /// <summary>Filenames the cleanup would rename, one issue per note and code, in path then code order.</summary>
    public IReadOnlyList<FilenameIssue> FindFilenameIssues()
    {
        var issues = new List<FilenameIssue>();
        foreach (var note in Entries.Where(n => ShouldLint(n) && !IsHubLike(n)))
        {
            var read = Read(note);
            if (read.Front.Error is { } error)
            {
                log?.Invoke($"Error inspecting filename for {note.FullPath}: {error}");
                continue;
            }

            var stem = note.Relative.Stem;
            var portability = LintRules.WindowsPortabilityIssueReason(note.Relative);
            string suggested;
            try
            {
                suggested = FilenameStem.Suggest(
                    stem,
                    title: Truthy(read.Front.Text("title")) ?? string.Empty,
                    sourceUrl: Truthy(read.Front.Text("source")) ?? Truthy(read.Front.Text("url")) ?? string.Empty,
                    relatedArticle: Truthy(read.Front.Text("related_article")) ?? string.Empty);
            }
            catch (FormatException exception)
            {
                // The Python let this ValueError escape and the whole lint crashed; one malformed URL now costs one note.
                log?.Invoke($"Error suggesting a filename for {note.FullPath}: {exception.Message}");
                continue;
            }

            if (portability is not null)
            {
                suggested = LintRules.PortableWindowsStem(suggested.Length > 0 ? suggested : stem);
            }

            if (suggested.Length == 0
                || !LintRules.NeedsFilenameCleanup(stem, note.Relative)
                || (suggested == stem && portability is null))
            {
                continue;
            }

            var suggestedPath = NotePath.Parse(string.Join('/', note.Relative.Parts.SkipLast(1).Append(suggested + note.Suffix)));
            foreach (var spec in LintRules.FilenameIssueSpecs(stem, suggested, note.Relative))
            {
                if (!IsIgnored(read.Ignored, spec.Code))
                {
                    issues.Add(new(note, spec.Code, spec.Reason, spec.Severity, suggested, suggestedPath));
                }
            }
        }

        // One issue per note and code, the last raised winning, ordered by path and then code.
        return [.. issues
            .GroupBy(issue => (issue.Note.FullPath, issue.Code))
            .Select(group => group.Last())
            .OrderBy(issue => issue.Note.FullPath, PyText.CodePointOrder)
            .ThenBy(issue => issue.Code, PyText.CodePointOrder)];
    }

    /// <summary>
    /// Notes sharing a normalized source URL, or a normalized title within one folder. A social post that links to the
    /// resource it shares a key with is a genuine link, not a duplicate.
    /// </summary>
    public IReadOnlyList<DuplicateGroup> FindDuplicates() => duplicates ??= Duplicates();

    private List<DuplicateGroup> Duplicates()
    {
        var byUrl = new Dictionary<string, List<VaultEntry>>(StringComparer.Ordinal);
        var urlOrder = new List<string>();
        var byTitle = new Dictionary<string, List<VaultEntry>>(StringComparer.Ordinal);
        var titleOrder = new List<string>();
        foreach (var note in Entries.Where(n => !Excluded(n.FullPath, ".obsidian")))
        {
            var read = Read(note);
            if (read.Front.Error is { } error)
            {
                log?.Invoke($"Error reading {note.FullPath}: {error}");
                continue;
            }

            var isSocial = read.Front.Text("type") == "social" || note.FullPath.Contains("Social Media Posts", StringComparison.Ordinal);
            if (Truthy(read.Front.Text("source")) is { } source)
            {
                string url;
                try
                {
                    url = VaultText.NormalizeUrl(source);
                }
                catch (FormatException exception)
                {
                    log?.Invoke($"Error reading {note.FullPath}: {exception.Message}");
                    continue;
                }

                if (url.Length > 0)
                {
                    Add(byUrl, urlOrder, url, note);
                }
            }

            var parent = note.Relative.Parts.Count > 1 ? note.Relative.ParentName : vaultName;
            if (isSocial || parent == note.Relative.Stem)
            {
                continue;
            }

            var title = Truthy(read.Front.Text("title")) ?? note.Relative.Stem;
            var normalized = PyText.Lower(PyText.Strip(ShortHash().Replace(CopySuffix().Replace(title, string.Empty, 1), string.Empty, 1)));
            Add(byTitle, titleOrder, $"title:{parent}/{normalized}", note);
        }

        var raw = new Dictionary<string, List<VaultEntry>>(StringComparer.Ordinal);
        var rawOrder = new List<string>();
        foreach (var key in urlOrder.Where(key => byUrl[key].Count > 1))
        {
            raw[key] = SortedSet(byUrl[key]);
            rawOrder.Add(key);
        }

        foreach (var key in titleOrder.Where(key => byTitle[key].Count > 1))
        {
            if (raw.TryGetValue(key, out var existing))
            {
                raw[key] = SortedSet([.. existing, .. byTitle[key]]);
                continue;
            }

            raw[key] = SortedSet(byTitle[key]);
            rawOrder.Add(key);
        }

        var groups = new List<DuplicateGroup>();
        foreach (var key in rawOrder)
        {
            var files = raw[key];
            var resources = files.Where(f => f.FullPath.Contains("Resources", StringComparison.Ordinal)).ToList();
            var social = files.Where(f => f.FullPath.Contains("Social Media Posts", StringComparison.Ordinal)).ToList();
            var kept = resources.Count > 0 && social.Count > 0
                ? [.. resources, .. social.Where(post => !LinksToAny(post, resources))]
                : files;
            if (kept.Count > 1)
            {
                groups.Add(new(key, kept));
            }
        }

        return groups;
    }

    // A social post is a genuine link when its related_article or its text holds [[name]] or [[stem]] of a resource.
    private bool LinksToAny(VaultEntry post, List<VaultEntry> resources)
    {
        var read = Read(post);
        var related = PyText.Strip((read.Front.Text("related_article") ?? string.Empty).Replace('\n', ' '));
        return resources.Any(resource =>
        {
            var byName = $"[[{resource.Relative.Name}]]";
            var byStem = $"[[{resource.Relative.Stem}]]";
            return related.Contains(byName, StringComparison.Ordinal) || related.Contains(byStem, StringComparison.Ordinal)
                || read.Content.Contains(byName, StringComparison.Ordinal) || read.Content.Contains(byStem, StringComparison.Ordinal);
        });
    }

    /// <summary>Findings and note counts per folder, each folder scored; keyed by the folder's vault-relative path.</summary>
    public IReadOnlyDictionary<string, FolderLintStats> FolderStats()
    {
        var folders = new Dictionary<string, FolderLintStats>(StringComparer.Ordinal);
        FolderLintStats For(VaultEntry note)
        {
            var parts = note.Relative.Parts;
            var key = parts.Count > 1 ? string.Join('/', parts.SkipLast(1)) : parts[0];
            if (!folders.TryGetValue(key, out var stats))
            {
                folders[key] = stats = new FolderLintStats();
            }

            return stats;
        }

        foreach (var failure in FindMetadataFailures())
        {
            var stats = For(failure.Note);
            if (failure.Severity == Warning)
            {
                stats.MetadataWarnings++;
            }
            else
            {
                stats.MetadataErrors++;
            }
        }

        foreach (var issue in FindFilenameIssues())
        {
            For(issue.Note).FilenameIssues++;
        }

        foreach (var note in FindDuplicates().SelectMany(group => group.Notes))
        {
            For(note).Duplicates++;
        }

        // The Python counted notes past only .git and Archive, so .obsidian's notes count towards their folder.
        foreach (var note in Entries.Where(n => !n.FullPath.Contains(".git", StringComparison.Ordinal) && !n.FullPath.Contains("Archive", StringComparison.Ordinal)))
        {
            For(note).TotalNotes++;
        }

        foreach (var stats in folders.Values)
        {
            stats.Rescore();
        }

        return folders;
    }

    /// <summary>Everything the lint finds, shaped as <c>vault graph lint --json</c> reports it.</summary>
    public LintReport Report()
    {
        var failures = FindMetadataFailures();
        var errors = failures.Where(f => f.Severity == Error).Select(Item).ToList();
        var warnings = failures.Where(f => f.Severity == Warning).Select(Item).ToList();
        // Issues arrive in path order, so each note's are adjacent and its first sets the group's severity.
        var filenames = FindFilenameIssues()
            .GroupBy(issue => issue.Note.Relative.ToString())
            .Select(group =>
            {
                var first = group.First();
                var note = first.Note.Relative;
                // Every rule the name breaks, ignored or not, as the Python's reason did.
                var reason = string.Join(", ", LintRules.FilenameIssueSpecs(note.Stem, first.SuggestedStem, note).Select(s => s.Reason));
                return new FilenameReport(group.Key, first.Severity, reason, first.SuggestedPath.ToString(), [.. group.Select(i => i.Code)]);
            })
            .OrderBy(report => report.Path, PyText.CodePointOrder);

        var found = FindDuplicates();
        return new LintReport(
            errors.Count == 0 && found.Count == 0,
            VaultPath,
            errors,
            warnings,
            [.. filenames],
            [.. found.Select(g => new KeyValuePair<string, IReadOnlyList<string>>(g.Key, [.. g.Notes.Select(n => n.Relative.ToString())]))]);
    }

    /// <summary>
    /// Markdown entries under each top-level folder of the vault (or of its <c>Vault</c> folder, when it has one), skipping
    /// dot-folders and empty ones, then <c>Total</c>. Ported from <c>rig.obsidian.utils.get_vault_stats</c>.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, int>> VaultStats(string vaultPath)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);
        var inner = Path.Join(vaultPath, "Vault");
        var root = File.Exists(inner) || Directory.Exists(inner) ? inner : vaultPath;
        var stats = new List<KeyValuePair<string, int>>();
        if (Directory.Exists(root))
        {
            foreach (var name in Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().Order(PyText.CodePointOrder))
            {
                var folder = Path.Join(root, name);
                if (name.StartsWith('.') || !Directory.Exists(folder))
                {
                    continue;
                }

                var count = VaultWalk.Markdown(folder).Count;
                if (count > 0)
                {
                    stats.Add(new(name, count));
                }
            }
        }

        stats.Add(new("Total", stats.Sum(s => s.Value)));
        return stats;
    }

    private static MetadataReportItem Item(MetadataFailure failure) =>
        new(failure.Note.Relative.ToString(), failure.Code, failure.Reason, failure.Severity);

    private static void Add(Dictionary<string, List<VaultEntry>> map, List<string> order, string key, VaultEntry note)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
            order.Add(key);
        }

        list.Add(note);
    }

    // sorted(set(paths)): Python orders paths by their parts, part by part in code-point order. Parts hold no NUL, and
    // NUL ranks below every other character, so joining on it orders the joined strings as the parts.
    private static List<VaultEntry> SortedSet(IEnumerable<VaultEntry> notes) =>
    [
        .. notes
            .DistinctBy(n => n.FullPath, StringComparer.Ordinal)
            .OrderBy(n => string.Join('\0', n.Relative.Parts), PyText.CodePointOrder),
    ];

    private static string? Truthy(string? text) => string.IsNullOrEmpty(text) ? null : text;

    internal static bool Excluded(string fullPath, params string[] extra) =>
        fullPath.Contains(".git", StringComparison.Ordinal)
        || fullPath.Contains("Archive", StringComparison.Ordinal)
        || extra.Any(e => fullPath.Contains(e, StringComparison.Ordinal));

    internal static bool ShouldLint(VaultEntry note) =>
        !Excluded(note.FullPath, ".obsidian") && LintRoots.Any(root => note.Relative.ToString().Contains(root, StringComparison.Ordinal));

    private bool IsHubLike(VaultEntry note)
    {
        var stem = note.Relative.Stem;
        var parts = note.Relative.Parts;
        var parent = parts.Count > 1 ? note.Relative.ParentName : vaultName;
        return HubStems.Contains(stem)
            || stem.Contains("Index", StringComparison.Ordinal)
            || stem.Contains("Hub", StringComparison.Ordinal)
            || stem.StartsWith("Inbox-", StringComparison.Ordinal)
            || parent == stem
            || parent + " Index" == stem
            || (parts.Count == 2 && HubRoots.Contains(parts[0]));
    }

    private static bool IsIgnored(HashSet<string> ignored, string code) =>
        ignored.Contains(code) || ignored.Contains("ALL") || ignored.Contains("TVS");

    private NoteRead Read(VaultEntry note)
    {
        if (reads.TryGetValue(note.FullPath, out var cached))
        {
            return cached;
        }

        NoteRead read;
        try
        {
            var content = Frontmatter.Decode(File.ReadAllBytes(note.FullPath));
            read = new NoteRead(Frontmatter.Parse(content), content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            read = new NoteRead(new FrontmatterRead(new Dictionary<string, PyValue>(), exception.Message), string.Empty);
        }

        reads[note.FullPath] = read;
        return read;
    }

    // `\s+\(1\)$` and `-[a-f0-9]{4}$`, each `$` also matching before a final newline as Python's does.
    [GeneratedRegex(@"[" + PyText.Space + @"]+\(1\)$")]
    private static partial Regex CopySuffix();

    [GeneratedRegex("-[a-f0-9]{4}$")]
    private static partial Regex ShortHash();

    private sealed record NoteRead(FrontmatterRead Front, string Content)
    {
        public HashSet<string> Ignored { get; } = IgnoredCodes(Front);

        // `meta.lint_ignore or meta.lint_ignore_kebab`: a str or list counts only when non-empty, as Python's `or` does.
        private static HashSet<string> IgnoredCodes(FrontmatterRead front)
        {
            var value = new[] { front.Get("lint_ignore"), front.Get("lint-ignore") }.FirstOrDefault(v => v is PyStr { Text.Length: > 0 } or PyList { Items.Count: > 0 });
            IEnumerable<PyValue> items = value switch
            {
                PyStr text => [text],
                PyList list => list.Items,
                _ => [],
            };
            var codes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                var text = item switch
                {
                    PyStr s => s.Text,
                    PyScalar s => s.Rendered,
                    PyNone => "None",
                    _ => null, // a nested list or mapping renders as a repr, which can never name a code
                };
                if (text is not null && PyText.Strip(text) is { Length: > 0 } code)
                {
                    codes.Add(code.ToUpperInvariant());
                }
            }

            return codes;
        }
    }
}

/// <summary>A metadata finding as the report shows it.</summary>
/// <param name="Path">The note's vault-relative path.</param>
/// <param name="Code">The finding's code.</param>
/// <param name="Reason">What is wrong.</param>
/// <param name="Severity"><c>error</c> or <c>warning</c>.</param>
public sealed record MetadataReportItem(string Path, string Code, string Reason, string Severity);

/// <summary>A note's filename findings, grouped.</summary>
/// <param name="Path">The note's vault-relative path.</param>
/// <param name="Severity">The first finding's severity.</param>
/// <param name="Reason">Every rule the name breaks.</param>
/// <param name="SuggestedPath">The note's vault-relative path after the cleanup's rename.</param>
/// <param name="Codes">The findings' codes, in code order.</param>
public sealed record FilenameReport(string Path, string Severity, string Reason, string SuggestedPath, IReadOnlyList<string> Codes);

/// <summary>The lint's findings, shaped as <c>vault graph lint --json</c> reports them.</summary>
/// <param name="Ok">Whether there are no metadata errors and no duplicates.</param>
/// <param name="VaultPathText">The vault linted.</param>
/// <param name="Errors">Metadata errors.</param>
/// <param name="Warnings">Metadata warnings.</param>
/// <param name="Filenames">Filename findings per note, in path order.</param>
/// <param name="Duplicates">Duplicate groups, each key and its notes' vault-relative paths.</param>
public sealed record LintReport(
    bool Ok,
    string VaultPathText,
    IReadOnlyList<MetadataReportItem> Errors,
    IReadOnlyList<MetadataReportItem> Warnings,
    IReadOnlyList<FilenameReport> Filenames,
    IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Duplicates)
{
    /// <summary>The report as Python's <c>json.dumps(asdict(report), indent=2, ensure_ascii=False)</c> writes it.</summary>
    public string ToJson()
    {
        var json = new PyJsonWriter();
        json.Object(() =>
        {
            json.Key("ok").Bool(Ok);
            json.Key("vault_path").String(VaultPathText);
            json.Key("metadata").Object(() =>
            {
                json.Key("errors").Array(Errors, Item);
                json.Key("warnings").Array(Warnings, Item);
            });
            json.Key("filenames").Array(Filenames, f => json.Object(() =>
            {
                json.Key("path").String(f.Path);
                json.Key("severity").String(f.Severity);
                json.Key("reason").String(f.Reason);
                json.Key("suggested_path").String(f.SuggestedPath);
                json.Key("codes").Array(f.Codes, c => json.String(c));
            }));
            json.Key("duplicates").Object(() =>
            {
                foreach (var (key, paths) in Duplicates)
                {
                    json.Key(key).Array(paths, p => json.String(p));
                }
            });
        });
        return json.ToString();

        void Item(MetadataReportItem item) => json.Object(() =>
        {
            json.Key("path").String(item.Path);
            json.Key("code").String(item.Code);
            json.Key("reason").String(item.Reason);
            json.Key("severity").String(item.Severity);
        });
    }

    /// <summary>
    /// The report as <c>vault graph lint</c> prints it, ported from <c>VaultLinter.run_lint</c>: each line ends in a newline,
    /// and a section after the first starts with a blank line.
    /// </summary>
    public string ToText()
    {
        var text = new StringBuilder();
        if (Errors.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"Found {Errors.Count} metadata errors:\n");
            Items(Errors);
        }

        if (Warnings.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nFound {Warnings.Count} metadata warnings:\n");
            Items(Warnings);
        }

        if (Filenames.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nFound {Filenames.Sum(f => f.Codes.Count)} filename issues:\n");
            foreach (var f in Filenames)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - {string.Join(", ", f.Codes)} {f.Path}: {f.Reason} -> suggest {Name(f.SuggestedPath)}\n");
            }
        }

        if (Duplicates.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nFound {Duplicates.Count} duplicate groups (excluding genuine links):\n");
            foreach (var (key, paths) in Duplicates)
            {
                text.Append('\n').Append(key).Append('\n');
                foreach (var path in paths)
                {
                    text.Append("  - ").Append(path).Append('\n');
                }
            }
        }

        if (Errors.Count == 0 && Duplicates.Count == 0)
        {
            text.Append("✓ Vault is clean. (Required metadata present, no duplicates detected).\n");
        }

        return text.ToString();

        void Items(IReadOnlyList<MetadataReportItem> items)
        {
            foreach (var item in items)
            {
                text.Append(CultureInfo.InvariantCulture, $"  - {item.Code} {item.Reason}: {Name(item.Path)}\n");
            }
        }

        static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    }
}

/// <summary>Writes JSON as Python's <c>json.dumps(..., indent=2, ensure_ascii=False)</c> does.</summary>
internal sealed class PyJsonWriter
{
    private readonly StringBuilder text = new();
    private readonly Stack<int> counts = new();
    private bool afterKey;

    /// <summary>Writes an object whose members <paramref name="members"/> writes.</summary>
    public PyJsonWriter Object(Action members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return Container('{', '}', members);
    }

    /// <summary>Writes an array of <paramref name="items"/>, each written by <paramref name="write"/>.</summary>
    public PyJsonWriter Array<T>(IEnumerable<T> items, Action<T> write)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(write);
        return Container('[', ']', () =>
        {
            foreach (var item in items)
            {
                write(item);
            }
        });
    }

    /// <summary>Writes an object member's key; its value comes next.</summary>
    public PyJsonWriter Key(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        Separate();
        Quote(key);
        text.Append(": ");
        afterKey = true;
        return this;
    }

    /// <summary>Writes a string.</summary>
    public PyJsonWriter String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Separate();
        Quote(value);
        return this;
    }

    /// <summary>Writes <c>true</c> or <c>false</c>.</summary>
    public PyJsonWriter Bool(bool value)
    {
        Separate();
        text.Append(value ? "true" : "false");
        return this;
    }

    /// <summary>Writes <c>null</c>.</summary>
    public PyJsonWriter Null()
    {
        Separate();
        text.Append("null");
        return this;
    }

    /// <summary>Writes an integer.</summary>
    public PyJsonWriter Number(long value)
    {
        Separate();
        text.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>Writes an unsigned integer (an inode can exceed <see cref="long.MaxValue"/>).</summary>
    public PyJsonWriter Number(ulong value)
    {
        Separate();
        text.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    /// <inheritdoc/>
    public override string ToString() => text.ToString();

    private PyJsonWriter Container(char open, char close, Action body)
    {
        Separate();
        text.Append(open);
        counts.Push(0);
        body();
        var count = counts.Pop();
        if (count > 0)
        {
            Newline(counts.Count);
        }

        text.Append(close);
        return this;
    }

    // Before a value: nothing after a key; otherwise a comma after an earlier sibling, then a newline and indent.
    private void Separate()
    {
        if (afterKey)
        {
            afterKey = false;
            return;
        }

        if (counts.Count == 0)
        {
            return;
        }

        var count = counts.Pop();
        if (count > 0)
        {
            text.Append(',');
        }

        counts.Push(count + 1);
        Newline(counts.Count);
    }

    private void Newline(int depth) => text.Append('\n').Append(' ', depth * 2);

    private void Quote(string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case < ' ': text.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: text.Append(c); break;
            }
        }

        text.Append('"');
    }
}
