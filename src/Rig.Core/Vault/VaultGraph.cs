using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>A wikilink whose target names no note.</summary>
/// <param name="Source">The note holding the link, relative to the vault.</param>
/// <param name="Target">The link's target, its alias and heading cut off.</param>
/// <param name="Line">The 1-based line the link is on, counting lines as Python's <c>splitlines</c> does.</param>
/// <param name="Context">That line, stripped.</param>
public sealed record BrokenLink(string Source, string Target, int Line, string Context);

/// <summary>A note's title appearing in another note's text without a wikilink.</summary>
/// <param name="Source">The note mentioning the title.</param>
/// <param name="Mention">The title mentioned.</param>
/// <param name="Target">The note with that title, relative to the vault, every <c>.md</c> removed.</param>
public sealed record UnlinkedMention(string Source, string Mention, string Target);

/// <summary>What <c>graph links list</c> found.</summary>
public sealed record LinkReport(IReadOnlyList<BrokenLink> Broken, IReadOnlyList<UnlinkedMention> Unlinked);

/// <summary>What <c>graph links fix</c> did: the broken links found and how many it rewrote.</summary>
public sealed record FixLinksResult(int BrokenFound, int Fixed);

/// <summary>What <c>graph orphans link</c> did: the orphans found and how many it linked from a parent hub.</summary>
public sealed record OrphanLinkResult(int OrphansFound, int Linked);

/// <summary>What <c>graph hubs</c> did: the hub notes it created and the links it added to them.</summary>
public sealed record RootHubResult(int HubsCreated, int LinksAdded);

/// <summary>
/// The vault's link graph, ported from <c>rig.obsidian.link_manager</c> and <c>orphan_manager</c>: broken wikilinks and
/// unlinked mentions, the notes nothing links to, and the PARA hub notes that gather them. A note is a file whose name ends
/// in <c>.md</c>, matched case-sensitively, found without entering symlinked folders. Links are compared as the Python
/// compared them, including its quirks: a note's path loses every <c>.md</c> in it, not just its suffix, and exclusions are
/// substrings, so <c>Home</c> excludes <c>Homework.md</c>.
/// </summary>
/// <param name="root">
/// The vault. <see cref="ListLinks"/> and <see cref="FixLinks"/> look in its <c>Vault</c> folder instead when it has one, as
/// the Python did.
/// </param>
/// <param name="say">Receives each line of output, as plain text: a wikilink is printed as written.</param>
/// <param name="native">The desktop app's own orphan list, preferred when the app has this vault open.</param>
public sealed partial class VaultGraph(string root, Action<string> say, ObsidianOrphans native)
{
    /// <summary>How many unlinked mentions <see cref="ListLinks"/> prints before summing up the rest.</summary>
    public const int MentionsShown = 20;

    // Path substrings that keep a note off the orphan list.
    private static readonly string[] Excluded = ["Templates/", "README.md", "Archive/", "Root File", "Home", ".obsidian/"];
    private static readonly string[] ParaFolders = ["Projects", "Areas", "Resources", "Archives"];
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly UTF8Encoding Strict = new(false, true);
    private static readonly Encoding Lenient = Encoding.GetEncoding("utf-8", EncoderFallback.ReplacementFallback, new DecoderReplacementFallback(string.Empty));

    /// <summary>The broken wikilinks in <paramref name="vault"/>, by note in code-point order, then by line.</summary>
    public static IReadOnlyList<BrokenLink> FindBrokenLinks(string vault)
    {
        var notes = Notes(vault);
        var paths = notes.Select(n => n.Bare).ToHashSet(StringComparer.Ordinal);
        var stems = ByStem(notes);
        var broken = new List<BrokenLink>();
        foreach (var note in notes.OrderBy(n => n.Relative, PyText.CodePointOrder))
        {
            if (Read(note) is not { } text)
            {
                continue;
            }

            var lines = PyText.SplitLines(text);
            for (var i = 0; i < lines.Count; i++)
            {
                foreach (Match match in WikiLink().Matches(lines[i]))
                {
                    var target = PyText.Strip(match.Groups[1].Value.Split('|')[0].Split('#')[0]);
                    if (target.Length > 0 && !Resolves(target, paths, stems))
                    {
                        broken.Add(new BrokenLink(note.Relative, target, i + 1, PyText.Strip(lines[i])));
                    }
                }
            }
        }

        return broken;
    }

    /// <summary>
    /// Titles longer than four characters that a note's text holds, outside any wikilink, between spaces or before a full
    /// stop. A title two notes share points at the later of them in code-point order.
    /// </summary>
    public static IReadOnlyList<UnlinkedMention> FindUnlinkedMentions(string vault)
    {
        var notes = Notes(vault).OrderBy(n => n.Relative, PyText.CodePointOrder).ToList();
        var titles = new Dictionary<string, Note>(StringComparer.Ordinal);
        foreach (var note in notes.Where(n => PyText.Length(n.Stem) > 4))
        {
            titles[note.Stem] = note;
        }

        var unlinked = new List<UnlinkedMention>();
        foreach (var note in notes)
        {
            if (Read(note) is not { } text)
            {
                continue;
            }

            var prose = WikiLink().Replace(text, " ");
            foreach (var (title, target) in titles)
            {
                if (title != note.Stem
                    && (prose.Contains($" {title} ", StringComparison.Ordinal) || prose.Contains($" {title}.", StringComparison.Ordinal)))
                {
                    unlinked.Add(new UnlinkedMention(note.Relative, title, target.Bare));
                }
            }
        }

        return unlinked;
    }

    /// <summary><c>graph links list</c>: prints the broken links and up to <see cref="MentionsShown"/> unlinked mentions.</summary>
    public LinkReport ListLinks()
    {
        var vault = LinkVault();
        var broken = FindBrokenLinks(vault);
        var unlinked = FindUnlinkedMentions(vault);
        if (broken.Count > 0)
        {
            say("Broken Links:");
            foreach (var link in broken)
            {
                say($"  {link.Source}:{link.Line} -> [[{link.Target}]]");
            }
        }

        if (unlinked.Count > 0)
        {
            say("\nUnlinked Mentions:");
            foreach (var mention in unlinked.Take(MentionsShown))
            {
                say($"  {mention.Source} mentions [[{mention.Mention}]]");
            }

            if (unlinked.Count > MentionsShown)
            {
                say($"  ... and {unlinked.Count - MentionsShown} more.");
            }
        }

        return new LinkReport(broken, unlinked);
    }

    /// <summary>
    /// <c>graph links fix --apply</c>: points each broken link whose last segment is the title of exactly one note at that
    /// note. Only the link text changes; line endings and every other byte stay as they were. A note that cannot be read as
    /// UTF-8 or written is reported and left alone, and its links are not counted as fixed.
    /// </summary>
    public FixLinksResult FixLinks()
    {
        var vault = LinkVault();
        var stems = ByStem(Notes(vault));
        var broken = FindBrokenLinks(vault);
        var rewrites = new Dictionary<string, List<(string Old, string New)>>(StringComparer.Ordinal);
        var fixedCount = 0;
        foreach (var link in broken)
        {
            if (stems.TryGetValue(link.Target.Split('/')[^1], out var matches) && matches.Count == 1)
            {
                say($"Fixing {link.Source}: [[{link.Target}]] -> [[{matches[0]}]]");
                if (!rewrites.TryGetValue(link.Source, out var fixes))
                {
                    rewrites[link.Source] = fixes = [];
                }

                fixes.Add(($"[[{link.Target}]]", $"[[{matches[0]}]]"));
                fixedCount++;
            }
        }

        foreach (var (relative, fixes) in rewrites)
        {
            var path = Path.Join(vault, relative);
            try
            {
                var text = Strict.GetString(File.ReadAllBytes(path));
                foreach (var (old, replacement) in fixes)
                {
                    text = text.Replace(old, replacement, StringComparison.Ordinal);
                }

                File.WriteAllText(path, text, Utf8);
            }
            catch (Exception exception) when (exception is DecoderFallbackException or IOException or UnauthorizedAccessException)
            {
                var reason = exception is DecoderFallbackException ? "it is not UTF-8" : exception.Message;
                say($"Skipped {relative}, its links left as they were: {reason}");
                fixedCount -= fixes.Count;
            }
        }

        return new FixLinksResult(broken.Count, fixedCount);
    }

    /// <summary>
    /// The notes nothing links to, in code-point order: the desktop app's list when it has this vault open, else every note no
    /// wikilink or embed names by path or title, plain, percent-decoded or before a heading.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindOrphansAsync(CancellationToken cancellationToken = default)
    {
        var listed = await native.ListAsync(root, cancellationToken).ConfigureAwait(false);
        if (listed.Count > 0)
        {
            return [.. listed.Where(o => !IsExcluded(o)).Order(PyText.CodePointOrder)];
        }

        var notes = Notes(root);
        var named = References(notes);
        return [.. notes
            .Where(n => !named.Contains(n.Bare) && !named.Contains(n.Stem) && !IsExcluded(n.Relative))
            .Select(n => n.Relative)
            .Order(PyText.CodePointOrder)];
    }

    /// <summary><c>graph orphans list</c>: prints each orphan.</summary>
    public async Task<IReadOnlyList<string>> ListOrphansAsync(CancellationToken cancellationToken = default)
    {
        var orphans = await FindOrphansAsync(cancellationToken).ConfigureAwait(false);
        foreach (var orphan in orphans)
        {
            say(orphan);
        }

        return orphans;
    }

    /// <summary>
    /// <c>graph orphans link --apply</c>: appends a link to each orphan to its nearest ancestor hub, the <c>X.md</c> beside a
    /// folder <c>X</c> holding it. Like the Python it does not look for the link first, so a second run, which finds the same
    /// orphans, appends it again.
    /// </summary>
    public async Task<OrphanLinkResult> LinkOrphansAsync(CancellationToken cancellationToken = default)
    {
        var orphans = await FindOrphansAsync(cancellationToken).ConfigureAwait(false);
        var linked = 0;
        foreach (var orphan in orphans)
        {
            if (LinkToParent(orphan) is { } hub)
            {
                linked++;
                say($"Linked {orphan} -> {hub}");
            }
        }

        return new OrphanLinkResult(orphans.Count, linked);
    }

    /// <summary>
    /// <c>graph hubs --apply</c>: creates <c>Home.md</c> and a hub note beside each PARA folder there is, links each new hub
    /// from Home, and links each orphan directly inside a PARA folder from that folder's hub.
    /// </summary>
    public async Task<RootHubResult> RootHubsAsync(CancellationToken cancellationToken = default)
    {
        var home = Path.Join(root, "Home.md");
        var created = Create(home, "# 🏠 Home\n\n## PARA Root\n") ? 1 : 0;
        var added = 0;
        foreach (var folder in ParaFolders.Where(f => Exists(Path.Join(root, f))))
        {
            var hub = Path.Join(root, $"{folder}.md");
            if (Create(hub, $"# 🗺️ {folder}\n\n[[Home|🔙 Back to Home]]\n\n## Content\n"))
            {
                created++;
                AppendUnless(home, $"[[{folder}]]", $"- [[{folder}]]\n");
            }

            foreach (var orphan in await FindOrphansAsync(cancellationToken).ConfigureAwait(false))
            {
                if (orphan.Split('/') is [var parent, _] && parent == folder && AppendUnless(hub, $"[[{orphan}]]", $"- [[{orphan}]]\n"))
                {
                    added++;
                    say($"Linked {orphan} -> {folder}.md");
                }
            }
        }

        return new RootHubResult(created, added);
    }

    private static bool Create(string path, string text)
    {
        if (Exists(path))
        {
            return false;
        }

        File.WriteAllText(path, text, Utf8);
        return true;
    }

    private static bool AppendUnless(string path, string present, string text)
    {
        if (File.ReadAllText(path).Contains(present, StringComparison.Ordinal))
        {
            return false;
        }

        File.AppendAllText(path, text, Utf8);
        return true;
    }

    private string LinkVault() => Exists(Path.Join(root, "Vault")) ? Path.Join(root, "Vault") : root;

    private string? LinkToParent(string orphan)
    {
        var parts = orphan.Split('/');
        for (var i = parts.Length - 1; i > 0; i--)
        {
            var hub = string.Join('/', parts[..i]) + ".md";
            var path = Path.Join(root, hub);
            if (Exists(path))
            {
                try
                {
                    File.AppendAllText(path, $"\n- [[{orphan}]]", Utf8);
                    return hub;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private static bool IsExcluded(string path) => Excluded.Any(p => path.Contains(p, StringComparison.Ordinal));

    // Every path or title a wikilink or embed names, as written, percent-decoded, or before its heading.
    private static HashSet<string> References(List<Note> notes)
    {
        var known = notes.Select(n => n.Stem).Concat(notes.Select(n => n.Bare)).ToHashSet(StringComparer.Ordinal);
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in notes)
        {
            if (Read(note) is not { } text)
            {
                continue;
            }

            foreach (Match match in WikiLink().Matches(text))
            {
                var target = PyText.Strip(match.Groups[1].Value.Split('|')[0]);
                if (target.Length == 0)
                {
                    continue;
                }

                var decoded = PyUrl.Unquote(target);
                var heading = target.Contains('#', StringComparison.Ordinal) ? PyText.Strip(target.Split('#')[0]) : null;
                if (new[] { target, decoded, heading }.FirstOrDefault(t => t is not null && known.Contains(t)) is { } hit)
                {
                    named.Add(hit);
                }
            }
        }

        return named;
    }

    // A target resolves to a note path as written or to a title; a title only "resolves" to a non-empty path, as in the Python.
    private static bool Resolves(string target, HashSet<string> paths, Dictionary<string, List<string>> stems) =>
        paths.Contains(target) || (stems.TryGetValue(target, out var matches) && matches[0].Length > 0);

    private static Dictionary<string, List<string>> ByStem(List<Note> notes)
    {
        var stems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var note in notes)
        {
            if (!stems.TryGetValue(note.Stem, out var paths))
            {
                stems[note.Stem] = paths = [];
            }

            paths.Add(note.Bare);
        }

        return stems;
    }

    // Files named *.md, symlinks to files and dangling symlinks included, as os.walk lists them; never a folder.
    private static List<Note> Notes(string vault) =>
        [.. VaultWalk.Markdown(vault)
            .Where(e => !Directory.Exists(e.FullPath))
            .Select(e => new Note(e.FullPath, e.Relative.ToString(), e.Relative.Stem))];

    // Text as Python read it with errors="ignore": bytes that are not UTF-8 dropped, newlines made universal.
    private static string? Read(Note note)
    {
        try
        {
            return Lenient.GetString(File.ReadAllBytes(note.FullPath)).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Whether a path exists as pathlib's exists() saw it, following symlinks: a dangling one does not.
    private static bool Exists(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var final = info.LinkTarget is null ? info : info.ResolveLinkTarget(returnFinalTarget: true);
            return final is not null && (File.Exists(final.FullName) || Directory.Exists(final.FullName));
        }
        catch (IOException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"\[\[(.*?)\]\]")]
    private static partial Regex WikiLink();

    private sealed record Note(string FullPath, string Relative, string Stem)
    {
        // The path as the Python keyed links: every ".md" removed, not only the suffix.
        public string Bare { get; } = Relative.Replace(".md", string.Empty, StringComparison.Ordinal);
    }
}
