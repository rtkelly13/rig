using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>
/// The vault repairs the Python's <c>VaultLinter</c> wrote: filename cleanup with wikilink rewriting, type backfill,
/// metadata repair, and duplicate consolidation. Each prints what the Python printed, less Rich's markup, and logs what
/// the Python logged. Frontmatter is edited line by line (<see cref="FrontmatterEditor"/>) where the Python re-dumped
/// it, and a note the Python would have crashed on is logged and passed over.
/// </summary>
public sealed partial class VaultRepair
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly string[] RefinedTypes = ["note", "project"];
    private static readonly string[] ProjectNoteTypes = ["project-note", "design-note"];
    private static readonly string[] BroadTypes = ["project", "resource"];

    private readonly string vault;
    private readonly Action<string> say;
    private readonly Action<string>? log;
    private readonly TimeProvider time;

    /// <summary>Repairs over the vault at <paramref name="vaultPath"/>.</summary>
    /// <param name="vaultPath">The vault folder.</param>
    /// <param name="say">Told each line the Python printed; a leading newline stands for the blank line it printed first.</param>
    /// <param name="log">Told each error the Python logged.</param>
    /// <param name="time">The clock and local time zone; the system's when omitted.</param>
    public VaultRepair(string vaultPath, Action<string> say, Action<string>? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(vaultPath);
        ArgumentNullException.ThrowIfNull(say);
        vault = vaultPath;
        this.say = say;
        this.log = log;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Renames each note the lint flags to its suggested name and rewrites the wikilinks that named it.</summary>
    public void CleanupFilenames(bool dryRun)
    {
        var issues = new VaultLinter(vault, log).FindFilenameIssues().DistinctBy(i => i.Note.FullPath, StringComparer.Ordinal).ToList();
        var renamed = 0;
        var skipped = 0;
        foreach (var issue in issues)
        {
            var path = issue.Note.FullPath;
            var target = Path.Join(vault, issue.SuggestedPath.ToString());
            var shown = $"{issue.Note.Relative} -> {issue.SuggestedPath}";
            if (!Exists(path))
            {
                skipped++;
                continue;
            }

            if (Exists(target) && Path.GetFullPath(target) != Path.GetFullPath(path))
            {
                say($"Skipping rename: {shown} (target exists)");
                skipped++;
                continue;
            }

            if (dryRun)
            {
                say($"(Dry Run) Would rename: {shown}");
                renamed++;
                continue;
            }

            try
            {
                Move(path, target);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Error renaming {path}: {exception.Message}");
                skipped++;
                continue;
            }

            var updated = UpdateWikilinks(issue.Note.Relative, issue.SuggestedPath);
            say($"✓ Renamed: {shown} ({updated} files updated)");
            renamed++;
        }

        say($"\n{(dryRun ? "Found" : "Renamed")} {renamed} filename issues.");
        if (skipped > 0)
        {
            say($"Skipped {skipped} filename issues due to conflicts or missing files.");
        }
    }

    /// <summary>Sets the type a note's folder suggests where the note has none, or a broader one than suggested.</summary>
    public void BackfillTypes(bool dryRun)
    {
        var count = 0;
        foreach (var note in VaultWalk.Markdown(vault).Where(n => !VaultLinter.Excluded(n.FullPath, ".obsidian")))
        {
            if (LintRules.SuggestedType(note.Relative) is not { Length: > 0 } suggested)
            {
                continue;
            }

            if (ReadNote(note, "backfilling") is not ({ } text, { } front))
            {
                continue;
            }

            var current = front.Text("type");
            var needsUpdate = string.IsNullOrEmpty(current)
                || RefinedTypes.Contains(current)
                || (current == "resource" && suggested == "resource-hub")
                || (BroadTypes.Contains(current) && ProjectNoteTypes.Contains(suggested));
            if (!needsUpdate || Edit(note, text, [new("type", suggested)], "backfilling") is not { } edited)
            {
                continue;
            }

            if (dryRun)
            {
                say($"(Dry Run) Would update type from '{current ?? "None"}' to '{suggested}' for: {note.Relative.Name}");
            }
            else if (Write(note, edited, "backfilling"))
            {
                say($"✓ Updated type to '{suggested}': {note.Relative.Name}");
            }
            else
            {
                continue;
            }

            count++;
        }

        say($"\n{(dryRun ? "Found" : "Fixed")} {count} notes missing 'type'.");
    }

    /// <summary>
    /// Fills each linted note's type, status, created time and source from its folder, its file and its text, marks a
    /// resource with no link as a hub, and gives a social post its article's source.
    /// </summary>
    public void RepairMetadata(bool dryRun)
    {
        var count = 0;
        foreach (var note in VaultWalk.Markdown(vault).Where(VaultLinter.ShouldLint))
        {
            if (ReadNote(note, "repairing") is not ({ } text, { } front) || Repairs(note, text, front) is not { } changes)
            {
                continue;
            }

            if (changes.Count == 0 || Edit(note, text, changes, "repairing") is not { } edited)
            {
                continue;
            }

            if (dryRun)
            {
                say($"(Dry Run) Would repair metadata for: {note.Relative.Name}");
            }
            else if (Write(note, edited, "repairing"))
            {
                say($"✓ Repaired metadata for: {note.Relative.Name}");
            }
            else
            {
                continue;
            }

            count++;
        }

        say($"\n{(dryRun ? "Found" : "Repaired")} {count} notes with metadata issues.");
    }

    /// <summary>Points each social post in a duplicate group at the group's first resource.</summary>
    public void CleanupSocialDuplicates(bool dryRun)
    {
        foreach (var group in new VaultLinter(vault, log).FindDuplicates())
        {
            var resources = group.Notes.Where(n => n.FullPath.Contains("Resources", StringComparison.Ordinal)).ToList();
            var social = group.Notes.Where(n => n.FullPath.Contains("Social Media Posts", StringComparison.Ordinal)).ToList();
            if (resources.Count == 0 || social.Count == 0)
            {
                continue;
            }

            say($"\nRepairing Social Linking for: {group.Key}");
            var winner = resources[0];
            foreach (var post in social)
            {
                if (dryRun)
                {
                    say($"  (Dry Run) Would update social link in {post.Relative.Name} to point to {winner.Relative.Name}");
                    continue;
                }

                if (ReadNote(post, "repairing") is ({ } text, _)
                    && Edit(post, text, [new("related_article", $"[[{winner.Relative.Name}]]")], "repairing") is { } edited
                    && Write(post, edited, "repairing"))
                {
                    say($"  ✓ Repaired social linking in {post.Relative.Name}");
                }
            }
        }
    }

    /// <summary>Keeps the fullest resource in each duplicate group and moves the rest to <c>Archive/Duplicates</c>.</summary>
    public void CleanupResourceDuplicates(bool dryRun)
    {
        foreach (var group in new VaultLinter(vault, log).FindDuplicates())
        {
            var resources = group.Notes.Where(n => n.FullPath.Contains("Resources", StringComparison.Ordinal)).ToList();
            if (resources.Count <= 1)
            {
                continue;
            }

            say($"\nConsolidating Resources for: {group.Key}");
            var scored = new List<(VaultEntry Note, int Score)>();
            foreach (var note in resources.Where(n => Exists(n.FullPath)))
            {
                if (ReadText(note, "scoring") is not { } text)
                {
                    continue;
                }

                var penalty = note.Relative.Name.Contains("(1)", StringComparison.Ordinal) ? 1000 : 0;
                var bonus = note.FullPath.Contains("Inbox-Resources", StringComparison.Ordinal) ? 0 : 50;
                scored.Add((note, Words(text) + bonus - penalty));
            }

            if (scored.Count == 0)
            {
                continue;
            }

            var ranked = scored.OrderByDescending(s => s.Score).ToList();
            say($"  Winner: {ranked[0].Note.Relative} (Score: {ranked[0].Score})");
            foreach (var (loser, _) in ranked.Skip(1).Where(s => Exists(s.Note.FullPath)))
            {
                if (dryRun)
                {
                    say($"  (Dry Run) Would archive duplicate: {loser.Relative}");
                }
                else if (Archive(loser))
                {
                    say($"  ✓ Archived: {loser.Relative.Name}");
                }
            }
        }
    }

    /// <summary>The words Python's <c>re.findall(r"\w+", text)</c> finds: runs of letters, numbers and underscores.</summary>
    public static int Words(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var word = rune.Value == '_' || Rune.GetUnicodeCategory(rune) is <= UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
            count += word && !inWord ? 1 : 0;
            inWord = word;
        }

        return count;
    }

    /// <summary>
    /// <paramref name="content"/> with each wikilink to the note once at <paramref name="from"/> pointing at
    /// <paramref name="to"/>, by path where it named a path and with <c>.md</c> where it had one.
    /// </summary>
    public static string RewriteWikilinks(string content, NotePath from, NotePath to)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var oldRel = from.ToString();
        var newRel = to.ToString();
        var oldRelNoExt = oldRel[..^Suffix(from).Length];
        var newRelNoExt = newRel[..^Suffix(to).Length];
        string[] oldTargets = [from.Name, from.Stem, oldRel, oldRelNoExt];
        return Wikilink().Replace(content, match =>
        {
            var target = PyText.Strip(match.Groups[2].Value);
            if (!oldTargets.Contains(target, StringComparer.Ordinal))
            {
                return match.Value;
            }

            var md = target.EndsWith(".md", StringComparison.Ordinal);
            var replacement = target.Contains('/', StringComparison.Ordinal) ? (md ? newRel : newRelNoExt) : md ? to.Name : to.Stem;
            return match.Groups[1].Value + replacement + match.Groups[3].Value + match.Groups[4].Value;
        });
    }

    // The keys a repair sets, in the Python's order; null when the note had to be passed over.
    private List<KeyValuePair<string, string>>? Repairs(VaultEntry note, string text, FrontmatterRead front)
    {
        var current = new Dictionary<string, PyValue>(front.Metadata, StringComparer.Ordinal);
        var changes = new List<KeyValuePair<string, string>>();
        void Set(string key, string value)
        {
            current[key] = new PyStr(value);
            changes.Add(new(key, value));
        }

        bool IsEmpty(string key) => current.GetValueOrDefault(key) switch
        {
            null or PyNone => true,
            PyStr { Text: var s } => PyText.Strip(s).Length == 0,
            PyList { Items.Count: 0 } => true,
            _ => false,
        };

        var noteType = front.Text("type");
        if (string.IsNullOrEmpty(noteType) || noteType == "note")
        {
            // Every linted path suggests a type: ShouldLint admits only the roots SuggestedType names.
            noteType = LintRules.SuggestedType(note.Relative)!;
            Set("type", noteType);
        }

        if (IsEmpty("status"))
        {
            Set("status", "active");
        }

        if (IsEmpty("created"))
        {
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(note.FullPath), TimeSpan.Zero);
            Set("created", TimeZoneInfo.ConvertTime(modified, time.LocalTimeZone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        var source = LabelledSource().Match(text) is { Success: true } labelled ? labelled : BareSource().Match(text);
        if (source.Success && noteType == "resource" && IsEmpty("source"))
        {
            Set("source", source.Groups[1].Value);
        }

        if (noteType == "resource" && IsEmpty("source") && !source.Success && note.Relative.ToString().Contains("Resources", StringComparison.Ordinal))
        {
            noteType = "resource-hub";
            Set("type", noteType);
        }

        if (noteType == "social" && IsEmpty("source") && front.Text("related_article") is { Length: > 0 } article)
        {
            var name = PyText.Strip(LinkBrackets().Replace(article, "$1").Replace(".md", string.Empty, StringComparison.Ordinal));
            foreach (var resource in VaultWalk.Markdown(Path.Join(vault, "Resources")).Where(r => r.Relative.Stem == name))
            {
                var read = Frontmatter.Load(resource.FullPath);
                if (read.Error is { } error)
                {
                    log?.Invoke($"Error repairing {note.FullPath}: {error}");
                    return null;
                }

                if (read.Text("source") is { Length: > 0 } restored)
                {
                    Set("source", restored);
                    break;
                }
            }
        }

        return FrontmatterEditor.SameMetadata(current, front.Metadata) ? [] : changes;
    }

    private (string Text, FrontmatterRead Front)? ReadNote(VaultEntry note, string verb)
    {
        if (ReadText(note, verb) is not { } text)
        {
            return null;
        }

        var front = Frontmatter.Parse(text);
        if (front.Error is { } error)
        {
            log?.Invoke($"Error {verb} {note.FullPath}: {error}");
            return null;
        }

        return (text, front);
    }

    private string? ReadText(VaultEntry note, string verb)
    {
        try
        {
            return Frontmatter.Decode(File.ReadAllBytes(note.FullPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            log?.Invoke($"Error {verb} {note.FullPath}: {exception.Message}");
            return null;
        }
    }

    private string? Edit(VaultEntry note, string text, IReadOnlyList<KeyValuePair<string, string>> changes, string verb)
    {
        var edit = FrontmatterEditor.Set(text, changes);
        if (edit.Text is null)
        {
            log?.Invoke($"Error {verb} {note.FullPath}: {edit.Refusal}");
        }

        return edit.Text;
    }

    private bool Write(VaultEntry note, string text, string verb)
    {
        try
        {
            File.WriteAllText(note.FullPath, text, Utf8);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Error {verb} {note.FullPath}: {exception.Message}");
            return false;
        }
    }

    // Rewrites links in every note outside .git and .obsidian, after the rename, so a note's links to itself follow it.
    private int UpdateWikilinks(NotePath from, NotePath to)
    {
        var updated = 0;
        foreach (var note in VaultWalk.Markdown(vault))
        {
            if (note.FullPath.Contains(".git", StringComparison.Ordinal) || note.FullPath.Contains(".obsidian", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var content = Frontmatter.Decode(File.ReadAllBytes(note.FullPath));
                var rewritten = RewriteWikilinks(content, from, to);
                if (rewritten != content)
                {
                    File.WriteAllText(note.FullPath, rewritten, Utf8);
                    updated++;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                log?.Invoke($"Error updating links in {note.FullPath}: {exception.Message}");
            }
        }

        return updated;
    }

    private bool Archive(VaultEntry loser)
    {
        var archive = Path.Join(vault, "Archive", "Duplicates");
        var target = Path.Join(archive, loser.Relative.Name);
        try
        {
            Directory.CreateDirectory(archive);
            if (Exists(target))
            {
                var name = NotePath.Parse(loser.Relative.Name);
                target = Path.Join(archive, $"{name.Stem}-{time.GetUtcNow().ToUnixTimeSeconds()}{Suffix(name)}");
            }

            Move(loser.FullPath, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Error archiving {loser.FullPath}: {exception.Message}");
            return false;
        }
    }

    private static string Suffix(NotePath path) => path.Name[path.Stem.Length..];

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    // Only notes that read move, so never a *.md folder; a rename onto itself does nothing.
    private static void Move(string from, string to)
    {
        if (from != to)
        {
            File.Move(from, to);
        }
    }

    [GeneratedRegex(@"(!?\[\[)([^\]|]+)(\|[^\]]+)?(\]\])")]
    private static partial Regex Wikilink();

    // `Source:\s*(https?://[^\s\)]+)` under re.IGNORECASE, whose Unicode folding also matches ſ (U+017F) for s.
    [GeneratedRegex(@"[Ss\u017F][Oo][Uu][Rr][Cc][Ee]:[" + PyText.Space + @"]*([Hh][Tt][Tt][Pp][Ss\u017F]?://[^" + PyText.Space + @"\)]+)")]
    private static partial Regex LabelledSource();

    [GeneratedRegex(@"^(?:- )?(https?://[^" + PyText.Space + @"\)]+)", RegexOptions.Multiline)]
    private static partial Regex BareSource();

    [GeneratedRegex(@"\[\[(.*?)\]\]")]
    private static partial Regex LinkBrackets();
}
