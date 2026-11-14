using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Rig.Core.GitHub;
using Rig.Core.Vault;

namespace Rig.Core.ReviewQueue;

/// <summary>The v1 triage projection and its offline, read-only validator.</summary>
public static partial class QueueMarkdown
{
    /// <summary>The single generated block's opening marker.</summary>
    public const string Begin = "<!-- rig-triage:begin v1 -->";

    /// <summary>The generated block's closing marker.</summary>
    public const string End = "<!-- rig-triage:end -->";

    /// <summary>The v1 table columns.</summary>
    public const string Header = "| Rank | Kind | Reference | Title | Priority | Readiness | Lines | Created | Head |";

    /// <summary>The v1 table separator.</summary>
    public const string Divider = "| --- | --- | --- | --- | --- | --- | --- | --- | --- |";

    private static readonly string[] Readiness = ["clean", "behind", "blocked", "draft-or-unknown"];

    /// <summary>Stable bytes for identical snapshots, with separate PR and issue ranks.</summary>
    public static string Render(IEnumerable<QueueItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var snapshot = items.ToList();
        var rows = new List<string> { Begin, Header, Divider };
        foreach (var kind in new[] { "pr", "issue" })
        {
            var index = 0;
            foreach (var item in QueueOrder.Sort(snapshot.Where(item => item.Kind == kind)))
            {
                index++;
                var priority = item.Priority < 4 ? $"P{item.Priority.ToString(CultureInfo.InvariantCulture)}" : "unlabelled";
                var ready = kind == "pr" ? Readiness[item.Readiness] : "n/a";
                var title = Escape(Spaces().Replace(item.Title, " ").Trim(' '));
                rows.Add(string.Create(CultureInfo.InvariantCulture,
                    $"| {index} | {kind} | [{item.Reference}]({item.Url}) | {title} | {priority} | {ready} | {item.Lines} | {item.Created} | {(item.Head.Length == 0 ? "-" : item.Head)} |"));
            }
        }

        rows.Add(End);
        return string.Join('\n', rows);
    }

    /// <summary>Checks structure, identity, ranks and canonical order without network, database or filesystem access.</summary>
    public static void Verify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var block = Block(text);
        var rows = PyText.SplitLines(block);
        if (!rows.Take(3).SequenceEqual([Begin, Header, Divider]))
        {
            throw new FormatException("Invalid triage table header");
        }

        var items = new List<QueueItem>();
        var refs = new HashSet<PrRef>();
        foreach (var line in rows.Skip(3).SkipLast(1))
        {
            var cells = line.Split('|').Select(PyText.Strip).ToArray();
            if (cells.Length != 11 || cells[0].Length != 0 || cells[^1].Length != 0)
            {
                throw new FormatException("Each triage row must have nine columns");
            }

            var kind = cells[2];
            if (kind is not ("pr" or "issue") || !Unsigned(cells[1], out var position) || position < 1)
            {
                throw new FormatException("Invalid kind or rank");
            }

            var link = Link().Match(cells[3]);
            if (!link.Success)
            {
                throw new FormatException("Expected a GitHub reference link");
            }

            var reference = PrRef.Parse(link.Groups[1].Value);
            if (!refs.Add(reference))
            {
                throw new FormatException($"Duplicate item: {reference}");
            }

            if (cells[5] is not ("P0" or "P1" or "P2" or "P3" or "unlabelled"))
            {
                throw new FormatException("Invalid priority");
            }

            if (!Unsigned(cells[7], out var size) || cells[4].Length == 0 || !AwareTimestamp(cells[8]))
            {
                throw new FormatException("Invalid size, title or creation time");
            }

            var readiness = Array.IndexOf(Readiness, cells[6]);
            if ((kind == "pr" && readiness < 0) || (kind == "issue" && cells[6] != "n/a"))
            {
                throw new FormatException("Invalid readiness");
            }

            if ((kind == "pr" && !Head().IsMatch(cells[9])) || (kind == "issue" && cells[9] != "-"))
            {
                throw new FormatException("Invalid head commit");
            }

            var item = new QueueItem(reference.Slug, reference.Number, kind, WebUtility.HtmlDecode(cells[4]), "OPEN", cells[8], cells[8],
                cells[9] == "-" ? "" : cells[9], cells[5] == "unlabelled" ? 4 : cells[5][1] - '0', kind == "pr" ? readiness : 0, size);
            if (link.Groups[2].Value != item.Url.AbsoluteUri)
            {
                throw new FormatException("Reference URL does not match the item");
            }

            items.Add(item);
        }

        if (Render(items) != block)
        {
            throw new FormatException("Queue is not in canonical priority/readiness/size/age order or has invalid ranks");
        }
    }

    /// <summary>Replaces only the generated block, or creates the initial document. Always validates before returning.</summary>
    public static string Project(IEnumerable<QueueItem> items, string? previous = null)
    {
        var block = Render(items);
        string content;
        if (previous is not null)
        {
            var old = Block(previous);
            var start = previous.IndexOf(old, StringComparison.Ordinal);
            content = string.Concat(previous.AsSpan(0, start), block, previous.AsSpan(start + old.Length));
        }
        else
        {
            content = "# PR and Issue Triage Queue\n\n"
                + "Transient projection generated by rig; regenerate rather than commit or file in the vault.\n\n"
                + "Priority → readiness → changed lines → creation time → repository → number.\n"
                + "PRs precede the issue queue. Deferred and closed items remain in rig's history.\n\n" + block + "\n";
        }

        Verify(content);
        return content;
    }

    private static string Block(string text)
    {
        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start < 0 || end < 0 || text.IndexOf(Begin, start + Begin.Length, StringComparison.Ordinal) >= 0
            || text.IndexOf(End, end + End.Length, StringComparison.Ordinal) >= 0)
        {
            throw new FormatException("Expected exactly one v1 triage begin/end block");
        }

        if (end < start)
        {
            throw new FormatException("Triage markers are reversed");
        }

        return text[start..(end + End.Length)];
    }

    // html.escape(..., quote=True), including Python's spelling for apostrophes.
    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&#x27;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal);

    private static bool Unsigned(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool AwareTimestamp(string text) => Offset().IsMatch(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex("[" + PyText.Space + "]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\A\[([^\]]+)\]\(([^)]+)\)\z")]
    private static partial Regex Link();

    [GeneratedRegex(@"\A[0-9a-f]{40}\z")]
    private static partial Regex Head();

    [GeneratedRegex(@"(?:Z|[+-][0-9]{2}:?[0-9]{2}(?::?[0-9]{2}(?:\.[0-9]+)?)?)\z")]
    private static partial Regex Offset();
}
