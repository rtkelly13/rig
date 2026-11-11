using System.Text;
using System.Text.Json;
using Rig.Core.Vault;

namespace Rig.Core.Mail;

/// <summary>The newest message of one sampled thread.</summary>
/// <param name="Sender">Who sent it.</param>
/// <param name="Subject">Its subject.</param>
/// <param name="To">Its recipients.</param>
public sealed record SampleMessage(string Sender, string Subject, IReadOnlyList<string> To);

/// <summary>One sender's share of the sample, how much of it rules claim, and which rules.</summary>
/// <param name="Sender">The sender.</param>
/// <param name="Threads">Sampled threads from the sender.</param>
/// <param name="Claimed">How many of them a rule claims.</param>
/// <param name="Rules">The claiming rules' names, sorted.</param>
/// <param name="Approximate">Whether a claiming rule leans on a query or exclude, which cannot be judged offline.</param>
/// <param name="UnclaimedSubject">The subject of the first thread no rule claims, or null.</param>
public sealed record SenderRow(string Sender, int Threads, int Claimed, IReadOnlyList<string> Rules, bool Approximate, string? UnclaimedSubject)
{
    /// <summary>Every sampled thread from this sender is claimed by some rule.</summary>
    public bool FullyClaimed => Claimed == Threads;
}

/// <summary>
/// Which senders in a saved inbox sample no rule claims yet. Ported from <c>rig.mail.coverage</c>. The input is what the
/// Gmail connector's <c>search_threads</c> returns, saved to a file per page. Matching is local and approximate:
/// <c>from</c>, <c>to</c> and <c>subject</c> are checked the way Gmail broadly does (substring, case-insensitive); a rule that
/// leans on <c>query</c> or <c>exclude</c> cannot be evaluated offline, so its claims are flagged as approximate.
/// </summary>
public static class MailCoverage
{
    /// <summary>The newest message of every thread across the saved <c>search_threads</c> pages.</summary>
    /// <exception cref="InvalidDataException">A page is not JSON, or not an object.</exception>
    public static IReadOnlyList<SampleMessage> ReadSample(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var sample = new List<SampleMessage>();
        foreach (var path in paths)
        {
            sample.AddRange(ReadPage(File.ReadAllText(path, Encoding.UTF8), path.Replace('\\', '/')));
        }

        return sample;
    }

    /// <summary>Whether <paramref name="match"/> would select <paramref name="message"/>, judged on from, to and subject alone.</summary>
    public static bool Claims(MailMatch match, SampleMessage message)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(message);
        if (match.Senders.Count > 0 && !Contains(message.Sender, match.Senders))
        {
            return false;
        }

        if (match.To.Count > 0 && !message.To.Any(address => Contains(address, match.To)))
        {
            return false;
        }

        if (match.Subject.Count > 0 && !Contains(message.Subject, match.Subject))
        {
            return false;
        }

        return match.Senders.Count > 0 || match.To.Count > 0 || match.Subject.Count > 0;
    }

    /// <summary>Senders by thread count, busiest first (ties in order of appearance); each message goes to the first rule claiming it.</summary>
    public static IReadOnlyList<SenderRow> Coverage(MailRuleSet ruleset, IReadOnlyList<SampleMessage> sample)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        ArgumentNullException.ThrowIfNull(sample);
        return
        [
            .. sample.GroupBy(message => message.Sender, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .Select(group =>
                {
                    var owned = group.Select(message => (Message: message, Rule: ruleset.Rules.FirstOrDefault(rule => Claims(rule.Match, message)))).ToList();
                    var hits = owned.Where(o => o.Rule is not null).Select(o => o.Rule!).ToList();
                    return new SenderRow(
                        group.Key,
                        owned.Count,
                        hits.Count,
                        [.. hits.Select(rule => rule.Name).Distinct(StringComparer.Ordinal).Order(PyText.CodePointOrder)],
                        hits.Any(rule => rule.Match.Query.Length > 0 || rule.Match.Exclude.Length > 0),
                        owned.FirstOrDefault(o => o.Rule is null).Message?.Subject);
                }),
        ];
    }

    /// <summary>The coverage rows as indented JSON, field for field as the Python wrote it.</summary>
    public static string CoverageJson(IEnumerable<SenderRow> rows)
    {
        var w = new PyJsonWriter();
        return w.Array(rows, row => w.Object(() =>
        {
            _ = w.Key("sender").String(row.Sender).Key("threads").Number(row.Threads).Key("claimed").Number(row.Claimed)
                .Key("rules").Array(row.Rules, name => w.String(name)).Key("approximate").Bool(row.Approximate).Key("unclaimed_subject");
            _ = row.UnclaimedSubject is { } subject ? w.String(subject) : w.Null();
        })).ToString();
    }

    // A page read leniently, as the Python's Lenient fields did: a field of the wrong shape counts as missing.
    private static List<SampleMessage> ReadPage(string text, string where)
    {
        JsonElement page;
        try
        {
            using var document = JsonDocument.Parse(text);
            page = document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{where}: not JSON ({e.Message})", e);
        }

        if (page.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{where}: not a search_threads page (expected an object)");
        }

        var sample = new List<SampleMessage>();
        foreach (var thread in Objects(Field(page, "threads")) ?? [])
        {
            if (Objects(Field(thread, "messages")) is [.., var last])
            {
                sample.Add(new SampleMessage(
                    Str(Field(last, "sender")) ?? string.Empty,
                    Str(Field(last, "subject")) ?? string.Empty,
                    Strs(Field(last, "toRecipients")) ?? []));
            }
        }

        return sample;
    }

    // The last value under key, as Python's json keeps the last of a repeated key.
    private static JsonElement? Field(JsonElement element, string key)
    {
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
        {
            found = property.Name == key ? property.Value : found;
        }

        return found;
    }

    private static List<JsonElement>? Objects(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array && array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Object)
            ? [.. array.EnumerateArray()]
            : null;

    private static string? Str(JsonElement? value) => value is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static List<string>? Strs(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array && array.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)
            ? [.. array.EnumerateArray().Select(item => item.GetString()!)]
            : null;

    private static bool Contains(string haystack, IReadOnlyList<string> needles)
    {
        var lowered = PyText.Lower(haystack);
        return needles.Any(needle => lowered.Contains(PyText.Lower(PyText.Strip(needle, "\"")), StringComparison.Ordinal));
    }
}
