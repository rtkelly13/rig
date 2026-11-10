using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Cli.Gh.Threads;

/// <summary>One review thread, as <c>list</c> shows it. <see cref="Line"/> is null for a file-level thread.</summary>
internal sealed record ReviewThread(
    string Id,
    string Path,
    int? Line,
    bool Outdated,
    bool Resolved,
    string Author,
    string Summary,
    string Url,
    string Body)
{
    /// <summary><c>path:line</c>, or just the path for a file-level thread.</summary>
    public string Location => Line is { } line ? $"{Path}:{line.ToString(CultureInfo.InvariantCulture)}" : Path;
}

/// <summary>A thread plus the body of its newest comment, which tells <c>resolve</c> whether it already replied.</summary>
internal sealed record ObservedThread(ReviewThread Thread, string LatestBody);

/// <summary>A PR's head branch and every commit on it (the commits the PR itself adds).</summary>
internal sealed record PullRequestCommits(string HeadRef, string HeadOid, IReadOnlyList<string> Commits);

/// <summary>What <c>resolve</c> will do, or why it will not touch the thread.</summary>
internal abstract record ResolveDecision;

/// <summary>What <c>resolve</c> will do. <see cref="Reply"/> is null when the reply is already the newest comment.</summary>
internal sealed record ResolvePlan(ReviewThread Thread, string Sha, string? Reply, bool Resolve) : ResolveDecision
{
    /// <summary>True when the thread is already replied to and resolved: a second run changes nothing.</summary>
    public bool IsEmpty => Reply is null && !Resolve;
}

/// <summary>Why <c>resolve</c> will not touch the thread.</summary>
internal sealed record Refusal(string Reason) : ResolveDecision;

/// <summary>Review threads' one-line summaries and the pure plan behind <c>resolve</c>. No I/O.</summary>
internal static partial class ThreadModel
{
    /// <summary>Shortest SHA prefix <c>resolve</c> accepts; git's own default abbreviation.</summary>
    public const int MinShaLength = 7;

    /// <summary>Summaries are cut to this many characters (Unicode scalars, as Python counts them).</summary>
    public const int SummaryWidth = 120;

    /// <summary>
    /// A first paragraph shorter than this is a severity label (CodeRabbit's <c>⚠️ Potential issue | 🟠 Major</c>), so
    /// the next paragraph, the title, is joined to it.
    /// </summary>
    private const int LabelWidth = 40;

    /// <summary>
    /// The first line or so of a review comment, with bot badges, HTML and emphasis stripped. A badge image becomes its
    /// alt text in brackets, so Codex's <c>![P1 Badge](…)</c> reads <c>[P1]</c>.
    /// </summary>
    public static string Summarise(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = Details().Replace(HtmlComment().Replace(body, string.Empty), string.Empty);
        text = Image().Replace(text, Badge);
        text = Tag().Replace(text, string.Empty);
        var paragraphs = ParagraphBreak().Split(text)
            .Select(part => CollapseWhitespace(Emphasis().Replace(part, string.Empty)))
            .Where(part => part.Length > 0)
            .ToList();
        if (paragraphs.Count == 0)
        {
            return string.Empty;
        }

        var summary = paragraphs[0];
        if (Width(summary) < LabelWidth && paragraphs.Count > 1)
        {
            summary = $"{summary} — {paragraphs[1]}";
        }

        return Width(summary) <= SummaryWidth ? summary : Cut(summary, SummaryWidth - 1).TrimEnd() + "…";
    }

    /// <summary>The reply <c>resolve</c> posts: <c>Fixed in &lt;sha&gt;.</c> and the note, if any, as its own paragraph.</summary>
    public static string ReplyBody(string sha, string? note)
    {
        var text = $"Fixed in {sha}.";
        return string.IsNullOrWhiteSpace(note) ? text : $"{text}\n\n{note.Trim()}";
    }

    /// <summary>
    /// Decides what resolving <paramref name="threadId"/> with the fix <paramref name="sha"/> takes, or why it must not
    /// happen. The SHA gate always applies, even to a thread that is already resolved: the fix must be one of the PR's
    /// own commits, so a thread is never closed against a fix that was never pushed (or was rebased away).
    /// </summary>
    public static ResolveDecision PlanResolve(
        string threadId, string sha, string? note, IReadOnlyList<ObservedThread> threads, PullRequestCommits pr)
    {
        ArgumentNullException.ThrowIfNull(threads);
        var observed = threads.FirstOrDefault(item => item.Thread.Id == threadId);
        if (observed is null)
        {
            return new Refusal($"{threadId} is not a review thread on this pull request");
        }

        var found = MatchCommit(sha, pr);
        if (found is Refusal refusal)
        {
            return refusal;
        }

        var oid = ((Matched)found).Oid;
        var body = ReplyBody(oid, note);
        var replied = AlreadyReplied(observed.LatestBody, body, hasNote: !string.IsNullOrWhiteSpace(note));
        return new ResolvePlan(observed.Thread, oid, replied ? null : body, !observed.Thread.Resolved);
    }

    /// <summary>The full SHA of the PR commit <paramref name="sha"/> names (a full SHA or a unique prefix), or why there is none.</summary>
    public static ResolveDecision MatchCommit(string sha, PullRequestCommits pr)
    {
        ArgumentNullException.ThrowIfNull(sha);
        ArgumentNullException.ThrowIfNull(pr);
        var wanted = sha.Trim().ToLowerInvariant();
        if (wanted.Length < MinShaLength || !Hex().IsMatch(wanted))
        {
            return new Refusal($"{PyRepr.Of(sha)} is not a commit SHA (need at least {MinShaLength} hex characters)");
        }

        var matches = pr.Commits
            .Where(oid => oid.ToLowerInvariant().StartsWith(wanted, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return matches.Count switch
        {
            0 => new Refusal(
                $"{sha} is not a commit on the PR's head branch {pr.HeadRef} (head {Prefix(pr.HeadOid, MinShaLength)}); "
                + "push the fix before resolving the thread"),
            1 => new Matched(matches[0]),
            _ => new Refusal($"{sha} is ambiguous on {pr.HeadRef}: it matches {matches.Count} commits; give more of it"),
        };
    }

    /// <summary>The first <paramref name="length"/> characters of <paramref name="text"/>, or all of it when shorter.</summary>
    public static string Prefix(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>
    /// Whether the newest comment already says what <paramref name="body"/> says, so a retry need not post it again. It
    /// must match exactly (whitespace aside), or a changed <c>--note</c> would be dropped. With no note asked for, any
    /// earlier <c>Fixed in &lt;sha&gt;.</c> reply for the same SHA, with or without a note, already covers it.
    /// </summary>
    private static bool AlreadyReplied(string latest, string body, bool hasNote)
    {
        var newest = CollapseWhitespace(latest);
        var wanted = CollapseWhitespace(body);
        return newest == wanted || (!hasNote && newest.StartsWith(wanted, StringComparison.Ordinal));
    }

    private static string Badge(Match match)
    {
        var alt = match.Groups[1].Value.Trim();
        if (alt.EndsWith("Badge", StringComparison.Ordinal))
        {
            alt = alt[..^"Badge".Length].Trim();
        }

        return alt.Length > 0 ? $"[{alt}]" : string.Empty;
    }

    /// <summary>Python's <c>" ".join(text.split())</c>: runs of whitespace become one space, ends trimmed.</summary>
    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int Width(string text) => text.EnumerateRunes().Count();

    private static string Cut(string text, int runes)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(runes))
        {
            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"<details\b.*?</details>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Details();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(?<![\w*])[*_]+|[*_]+(?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();

    [GeneratedRegex("^[0-9a-fA-F]+$")]
    private static partial Regex Hex();
}

/// <summary>The PR commit a SHA named.</summary>
internal sealed record Matched(string Oid) : ResolveDecision;
