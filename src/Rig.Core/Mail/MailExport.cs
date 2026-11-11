using System.Globalization;
using System.Text;
using Rig.Core.Vault;

namespace Rig.Core.Mail;

/// <summary>One <c>&lt;apps:property&gt;</c> of a Gmail filter entry.</summary>
/// <param name="Name">The property's name.</param>
/// <param name="Value">Its value.</param>
public sealed record FilterProperty(string Name, string Value);

/// <summary>One connector pass: search <paramref name="Query"/>, then apply the actions to every thread found.</summary>
/// <param name="Rule">The rule's name.</param>
/// <param name="Query">The sweep search.</param>
/// <param name="Label">The label to add, or null.</param>
/// <param name="Archive">Take it out of the inbox.</param>
/// <param name="MarkRead">Mark it read.</param>
/// <param name="Star">Star it.</param>
/// <param name="Trash">Move it to the trash.</param>
/// <param name="Important">Mark it important, never important, or leave it (null).</param>
/// <param name="FilterCoversNewMail">Whether a Gmail filter handles new mail, so the sweep only clears the backlog.</param>
public sealed record SweepStep(
    string Rule, string Query, string? Label, bool Archive, bool MarkRead, bool Star, bool Trash, bool? Important, bool FilterCoversNewMail);

/// <summary>
/// A rule as a Gmail API filter (<c>users.settings.filters</c>), labels still by name. <paramref name="Add"/> and
/// <paramref name="Remove"/> hold system label ids (INBOX, UNREAD, STARRED, IMPORTANT, TRASH); <paramref name="Label"/> is
/// the user label to resolve to an id at create time.
/// </summary>
/// <param name="Rule">The rule's name.</param>
/// <param name="Sender">The <c>from</c> criterion.</param>
/// <param name="To">The <c>to</c> criterion.</param>
/// <param name="Subject">The <c>subject</c> criterion.</param>
/// <param name="Query">The <c>query</c> criterion.</param>
/// <param name="NegatedQuery">The <c>negatedQuery</c> criterion.</param>
/// <param name="Label">The user label to add, or null.</param>
/// <param name="Add">System labels to add.</param>
/// <param name="Remove">System labels to remove.</param>
public sealed record ApiFilter(
    string Rule, string Sender, string To, string Subject, string Query, string NegatedQuery, string? Label, IReadOnlyList<string> Add, IReadOnlyList<string> Remove);

/// <summary>Compiles rules into Gmail's filter-import XML, the connector's sweep plan and Gmail API filters. Ported from <c>rig.mail.export</c>.</summary>
public static class MailExport
{
    private const string Atom = "http://www.w3.org/2005/Atom";
    private const string Apps = "http://schemas.google.com/apps/2006";

    /// <summary>The criteria and actions of <paramref name="rule"/> as Gmail filter properties, in Gmail's own order.</summary>
    public static IReadOnlyList<FilterProperty> FilterProperties(MailRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var (match, actions) = (rule.Match, rule.Actions);
        var props = new List<FilterProperty>();
        void Add(string name, string value, bool when)
        {
            if (when)
            {
                props.Add(new FilterProperty(name, value));
            }
        }

        Add("from", Joined(match.Senders), match.Senders.Count > 0);
        Add("to", Joined(match.To), match.To.Count > 0);
        Add("subject", Joined(match.Subject), match.Subject.Count > 0);
        Add("hasTheWord", match.Query, match.Query.Length > 0);
        Add("doesNotHaveTheWord", match.Exclude, match.Exclude.Length > 0);
        Add("label", actions.Label ?? string.Empty, !string.IsNullOrEmpty(actions.Label));
        Add("shouldArchive", "true", actions.Archive);
        Add("shouldMarkAsRead", "true", actions.MarkRead);
        Add("shouldStar", "true", actions.Star);
        Add("shouldTrash", "true", actions.Trash);
        Add("shouldAlwaysMarkAsImportant", "true", actions.Important == true);
        Add("shouldNeverMarkAsImportant", "true", actions.Important == false);
        return props;
    }

    /// <summary>
    /// Gmail's filter export format, as Python's ElementTree wrote it; import it under Settings → Filters → Import filters.
    /// Sweep-only rules (<c>older_than</c>) are left out, since a filter cannot act on age.
    /// </summary>
    public static string FiltersXml(MailRuleSet ruleset, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        var stamp = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var xml = new StringBuilder("<?xml version='1.0' encoding='UTF-8'?>\n")
            .Append(CultureInfo.InvariantCulture, $"<feed xmlns=\"{Atom}\" xmlns:apps=\"{Apps}\">\n");
        Element(xml, 1, "title", "Mail Filters");
        Element(xml, 1, "id", "tag:mail.google.com,2008:filters:");
        Element(xml, 1, "updated", stamp);
        var index = 0;
        foreach (var rule in ruleset.Rules.Where(r => r.Exportable))
        {
            xml.Append("  <entry>\n    <category term=\"filter\" />\n");
            Element(xml, 2, "title", "Mail Filter");
            Element(xml, 2, "id", $"tag:mail.google.com,2008:filter:{index++}");
            Element(xml, 2, "updated", stamp);
            Element(xml, 2, "content", rule.Name);
            foreach (var prop in FilterProperties(rule))
            {
                xml.Append(CultureInfo.InvariantCulture, $"    <apps:property name=\"{Attribute(prop.Name)}\" value=\"{Attribute(prop.Value)}\" />\n");
            }

            xml.Append("  </entry>\n");
        }

        return xml.Append("</feed>\n").ToString();
    }

    /// <summary>The sweep for every rule, or just the one named <paramref name="only"/>.</summary>
    public static IReadOnlyList<SweepStep> SweepPlan(MailRuleSet ruleset, string? only = null)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return
        [
            .. ruleset.Rules
                .Where(rule => only is null || rule.Name == only)
                .Select(rule => new SweepStep(
                    rule.Name,
                    MailRuleSet.SweepQuery(rule),
                    rule.Actions.Label,
                    rule.Actions.Archive,
                    rule.Actions.MarkRead,
                    rule.Actions.Star,
                    rule.Actions.Trash,
                    rule.Actions.Important,
                    rule.Exportable)),
        ];
    }

    /// <summary>The plan as indented JSON for the skill to read, field for field as the Python wrote it.</summary>
    public static string SweepJson(IEnumerable<SweepStep> steps)
    {
        var w = new PyJsonWriter();
        return w.Array(steps, step => w.Object(() =>
        {
            _ = w.Key("rule").String(step.Rule).Key("query").String(step.Query);
            Optional(w.Key("label"), step.Label);
            _ = w.Key("archive").Bool(step.Archive).Key("mark_read").Bool(step.MarkRead).Key("star").Bool(step.Star).Key("trash").Bool(step.Trash);
            Optional(w.Key("important"), step.Important);
            _ = w.Key("filter_covers_new_mail").Bool(step.FilterCoversNewMail);
        })).ToString();
    }

    /// <summary>Every exportable rule as a Gmail API filter, matching <see cref="FiltersXml"/> field for field.</summary>
    public static IReadOnlyList<ApiFilter> ApiFilters(MailRuleSet ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return
        [
            .. ruleset.Rules.Where(r => r.Exportable).Select(rule =>
            {
                var (match, acts) = (rule.Match, rule.Actions);
                List<string> add = [.. Flags(("STARRED", acts.Star), ("IMPORTANT", acts.Important == true), ("TRASH", acts.Trash))];
                List<string> remove = [.. Flags(("INBOX", acts.Archive), ("UNREAD", acts.MarkRead), ("IMPORTANT", acts.Important == false))];
                return new ApiFilter(
                    rule.Name, Joined(match.Senders), Joined(match.To), Joined(match.Subject), match.Query, match.Exclude, acts.Label, add, remove);
            }),
        ];
    }

    /// <summary>The filters as indented JSON.</summary>
    public static string ApiFiltersJson(IEnumerable<ApiFilter> filters)
    {
        var w = new PyJsonWriter();
        return w.Array(filters, f => w.Object(() =>
        {
            _ = w.Key("rule").String(f.Rule).Key("sender").String(f.Sender).Key("to").String(f.To).Key("subject").String(f.Subject)
                .Key("query").String(f.Query).Key("negated_query").String(f.NegatedQuery);
            Optional(w.Key("label"), f.Label);
            _ = w.Key("add").Array(f.Add, label => w.String(label)).Key("remove").Array(f.Remove, label => w.String(label));
        })).ToString();
    }

    private static IEnumerable<string> Flags(params (string Name, bool On)[] flags) => flags.Where(f => f.On).Select(f => f.Name);

    private static string Joined(IReadOnlyList<string> values) =>
        string.Join(" OR ", values.Select(v => MailRuleSet.HasSpace(v) ? $"\"{v}\"" : v));

    private static void Optional(PyJsonWriter w, string? value) => _ = value is null ? w.Null() : w.String(value);

    private static void Optional(PyJsonWriter w, bool? value) => _ = value is { } on ? w.Bool(on) : w.Null();

    private static void Element(StringBuilder xml, int depth, string name, string text)
    {
        xml.Append(' ', depth * 2).Append('<').Append(name);
        _ = text.Length == 0 ? xml.Append(" />\n") : xml.Append('>').Append(Text(text)).Append("</").Append(name).Append(">\n");
    }

    private static string Text(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Attribute(string s) => Text(s)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("\r", "&#13;", StringComparison.Ordinal)
        .Replace("\n", "&#10;", StringComparison.Ordinal)
        .Replace("\t", "&#09;", StringComparison.Ordinal);
}
