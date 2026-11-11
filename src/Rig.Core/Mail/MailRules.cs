using System.Text;
using System.Text.RegularExpressions;
using Rig.Core.Vault;

namespace Rig.Core.Mail;

/// <summary>The rules file is missing, malformed, or describes a rule that cannot work.</summary>
public sealed class RulesException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RulesException()
    {
    }

    /// <summary>Creates the exception with its message.</summary>
    public RulesException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with its message and cause.</summary>
    public RulesException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What a rule selects. Every populated field must hold; the values inside a field are ORed.</summary>
/// <param name="Senders">The <c>from</c> values.</param>
/// <param name="To">The <c>to</c> values.</param>
/// <param name="Subject">The <c>subject</c> values.</param>
/// <param name="Query">A raw Gmail search, or empty.</param>
/// <param name="Exclude">A Gmail search the mail must not match, or empty.</param>
public sealed record MailMatch(
    IReadOnlyList<string> Senders, IReadOnlyList<string> To, IReadOnlyList<string> Subject, string Query = "", string Exclude = "")
{
    /// <summary>True when the rule would match every message.</summary>
    public bool IsEmpty => Senders.Count == 0 && To.Count == 0 && Subject.Count == 0 && Query.Length == 0;
}

/// <summary>What happens to a matching thread. Mirrors the actions a Gmail filter can take.</summary>
/// <param name="Label">The label to add, or null.</param>
/// <param name="Archive">Take it out of the inbox.</param>
/// <param name="MarkRead">Mark it read.</param>
/// <param name="Star">Star it.</param>
/// <param name="Important">Mark it important (true), never important (false), or leave it (null).</param>
/// <param name="Trash">Move it to the trash.</param>
public sealed record MailActions(
    string? Label = null, bool Archive = false, bool MarkRead = false, bool Star = false, bool? Important = null, bool Trash = false)
{
    /// <summary>True when the rule would do nothing.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Label) && !Archive && !MarkRead && !Star && !Trash && Important is null;

    /// <summary>True when a matching thread ends up out of the inbox.</summary>
    public bool LeavesInbox => Archive || Trash;
}

/// <summary>One named rule. <paramref name="OlderThan"/> makes it sweep-only: filters act on arrival, never on age.</summary>
/// <param name="Name">The rule's name, unique in the file.</param>
/// <param name="Match">What it selects.</param>
/// <param name="Actions">What it does.</param>
/// <param name="OlderThan">A Gmail age such as <c>7d</c>, or null.</param>
/// <param name="Note">A free-text note.</param>
public sealed record MailRule(string Name, MailMatch Match, MailActions Actions, string? OlderThan = null, string Note = "")
{
    /// <summary>Whether a Gmail filter can express this rule.</summary>
    public bool Exportable => OlderThan is null;
}

/// <summary>
/// The rules file and the Gmail search each rule stands for. Ported from <c>rig.mail.rules</c>. Rules apply in order; the
/// first rule that claims a sender wins in coverage.
/// </summary>
/// <param name="Rules">The rules, in file order.</param>
public sealed partial record MailRuleSet(IReadOnlyList<MailRule> Rules)
{
    /// <summary>The environment variable that names the rules file.</summary>
    public const string RulesVariable = "MAIL_RULES";

    /// <summary>
    /// <c>$MAIL_RULES</c> (with <c>~</c> expanded), else <c>rig/temp/mail/rules.yaml</c> under <paramref name="personal"/>, the
    /// main rig checkout's gitignored temp folder: the rules name real senders.
    /// </summary>
    public static string DefaultPath(Func<string, string?> environment, string home, string personal)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (environment(RulesVariable) is { Length: > 0 } configured)
        {
            return configured == "~" ? home : configured.StartsWith("~/", StringComparison.Ordinal) ? Path.Join(home, configured[2..]) : configured;
        }

        return Path.Join(personal, "rig", "temp", "mail", "rules.yaml");
    }

    /// <summary>Reads and validates the rules file at <paramref name="path"/>.</summary>
    /// <exception cref="RulesException">The file is missing or its rules are not valid.</exception>
    public static MailRuleSet Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var shown = path.Replace('\\', '/');
        if (!File.Exists(path))
        {
            throw new RulesException($"no rules file at {shown} (set --rules or ${RulesVariable})");
        }

        return Parse(File.ReadAllText(path, Encoding.UTF8), shown);
    }

    /// <summary>Decodes and validates rules text; every problem is a <see cref="RulesException"/> naming the rule.</summary>
    /// <exception cref="RulesException">The text is not YAML, does not fit the schema, or holds a rule that cannot work.</exception>
    public static MailRuleSet Parse(string text, string where = "rules")
    {
        ArgumentNullException.ThrowIfNull(text);
        PyValue? loaded;
        try
        {
            loaded = PyYaml.Load(text);
        }
        catch (PyYamlException e)
        {
            throw new RulesException($"{where}: not valid YAML ({e.Message})", e);
        }

        MailRuleSet ruleset;
        try
        {
            ruleset = Decode(loaded ?? PyNone.Value);
        }
        catch (InvalidDataException e)
        {
            throw new RulesException($"{where}: {e.Message}", e);
        }

        Validate(ruleset, where);
        return ruleset;
    }

    /// <summary>The Gmail search that selects what <paramref name="match"/> selects, as a filter would see it.</summary>
    public static string MatchQuery(MailMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var parts = new List<string>();
        if (match.Senders.Count > 0)
        {
            parts.Add(AnyOf("from", match.Senders));
        }

        if (match.To.Count > 0)
        {
            parts.Add(AnyOf("to", match.To));
        }

        if (match.Subject.Count > 0)
        {
            parts.Add(AnyOf("subject", match.Subject));
        }

        if (match.Query.Length > 0)
        {
            parts.Add($"({match.Query})");
        }

        if (match.Exclude.Length > 0)
        {
            // Gmail renders a filter's "Doesn't have" field as -{...}; match that exactly.
            parts.Add($"-{{{match.Exclude}}}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// The search for threads <paramref name="rule"/> has not acted on yet. A rule that clears the inbox only looks in the
    /// inbox. A rule that leaves mail in place looks everywhere for mail still missing any of its actions (label, star,
    /// read, importance), so adding an action to a rule catches up mail it already labelled, and a re-run is a no-op.
    /// </summary>
    public static string SweepQuery(MailRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var parts = new List<string> { MatchQuery(rule.Match) };
        if (rule.Actions.LeavesInbox)
        {
            parts.Add("in:inbox");
        }
        else
        {
            var unmet = UnmetTerms(rule.Actions);
            if (unmet.Count > 0)
            {
                parts.Add(unmet.Count == 1 ? unmet[0] : "{" + string.Join(' ', unmet) + "}");
            }
        }

        if (!string.IsNullOrEmpty(rule.OlderThan))
        {
            parts.Add($"older_than:{rule.OlderThan}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>Gmail's search form of a label name: stripped, lowercased, each run of whitespace and <c>/</c> one <c>-</c>.</summary>
    public static string SearchLabel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var text = new StringBuilder();
        var inRun = false;
        foreach (var c in PyText.Strip(name))
        {
            var separator = c == '/' || PyText.IsSpace(c);
            if (separator && !inRun)
            {
                text.Append('-');
            }
            else if (!separator)
            {
                text.Append(c);
            }

            inRun = separator;
        }

        return PyText.Lower(text.ToString());
    }

    /// <summary>Whether <paramref name="term"/> holds whitespace, so Gmail needs it quoted to read it as a phrase.</summary>
    internal static bool HasSpace(string term) => term.Any(PyText.IsSpace);

    // Gmail terms that each match mail still missing one of the actions; OR them to find work.
    private static List<string> UnmetTerms(MailActions actions)
    {
        var terms = new List<string>();
        if (!string.IsNullOrEmpty(actions.Label))
        {
            terms.Add($"-label:{SearchLabel(actions.Label)}");
        }

        if (actions.Star)
        {
            terms.Add("-is:starred");
        }

        if (actions.MarkRead)
        {
            terms.Add("is:unread");
        }

        if (actions.Important is { } important)
        {
            terms.Add(important ? "-is:important" : "is:important");
        }

        return terms;
    }

    private static string Quote(string term) => HasSpace(term) && !term.StartsWith('"') ? $"\"{term}\"" : term;

    private static string AnyOf(string op, IReadOnlyList<string> values)
    {
        var terms = values.Select(Quote).ToList();
        return $"{op}:{(terms.Count == 1 ? terms[0] : "(" + string.Join(" OR ", terms) + ")")}";
    }

    private static void Validate(MailRuleSet ruleset, string where)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in ruleset.Rules)
        {
            var prefix = $"{where}: rule `{rule.Name}`";
            if (!seen.Add(rule.Name))
            {
                throw new RulesException($"{prefix} is defined twice");
            }

            if (rule.Match.IsEmpty)
            {
                throw new RulesException($"{prefix} matches everything; give it from, to, subject or query");
            }

            if (rule.Actions.IsEmpty)
            {
                throw new RulesException($"{prefix} has no actions");
            }

            if (rule.OlderThan is { } age && !Duration().IsMatch(age))
            {
                throw new RulesException($"{prefix}: older_than must look like 3d, 2m or 1y, not {PyText.Repr(age)}");
            }
        }
    }

    // Python's re.match with ^...$: $ also matches before a final newline.
    [GeneratedRegex(@"^\d+[dmy]\n?\z")]
    private static partial Regex Duration();

    // The strict schema: strings must be strings, flags must be bools, and unknown keys are refused.
    private static MailRuleSet Decode(PyValue value)
    {
        var top = Fields(value, "RuleSet", ["rules"]);
        return new MailRuleSet(top.TryGetValue("rules", out var rules) ? Items(rules, "rules").Select((r, i) => DecodeRule(r, $"rules.{i}")).ToList() : []);
    }

    private static MailRule DecodeRule(PyValue value, string at)
    {
        var fields = Fields(value, at, ["name", "match", "actions", "older_than", "note"]);
        return new MailRule(
            Str(Required(fields, "name", at), $"{at}.name"),
            DecodeMatch(Required(fields, "match", at), $"{at}.match"),
            DecodeActions(Required(fields, "actions", at), $"{at}.actions"),
            fields.TryGetValue("older_than", out var age) ? OptionalStr(age, $"{at}.older_than") : null,
            fields.TryGetValue("note", out var note) ? Str(note, $"{at}.note") : string.Empty);
    }

    private static MailMatch DecodeMatch(PyValue value, string at)
    {
        var fields = Fields(value, at, ["from", "to", "subject", "query", "exclude"]);
        return new MailMatch(
            Strs(fields, "from", at),
            Strs(fields, "to", at),
            Strs(fields, "subject", at),
            fields.TryGetValue("query", out var query) ? Str(query, $"{at}.query") : string.Empty,
            fields.TryGetValue("exclude", out var exclude) ? Str(exclude, $"{at}.exclude") : string.Empty);
    }

    private static MailActions DecodeActions(PyValue value, string at)
    {
        var fields = Fields(value, at, ["label", "archive", "mark_read", "star", "important", "trash"]);
        bool Flag(string key) => fields.TryGetValue(key, out var v) && (Bool(v, $"{at}.{key}") ?? throw new InvalidDataException($"{at}.{key}: expected a boolean"));
        return new MailActions(
            fields.TryGetValue("label", out var label) ? OptionalStr(label, $"{at}.label") : null,
            Flag("archive"),
            Flag("mark_read"),
            Flag("star"),
            fields.TryGetValue("important", out var important) ? Bool(important, $"{at}.important") : null,
            Flag("trash"));
    }

    private static Dictionary<string, PyValue> Fields(PyValue value, string at, string[] known)
    {
        if (value is not PyDict dict)
        {
            throw new InvalidDataException($"{at}: expected a mapping");
        }

        var fields = new Dictionary<string, PyValue>(StringComparer.Ordinal);
        foreach (var (key, item) in dict.Entries)
        {
            if (key is not PyStr { Text: var name } || !known.Contains(name, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"{at}: unexpected key {Shown(key)}; expected one of {string.Join(", ", known)}");
            }

            fields[name] = item;
        }

        return fields;
    }

    private static string Shown(PyValue key) => key switch
    {
        PyStr s => s.Text,
        PyScalar s => s.Rendered,
        PyNone => "None",
        _ => "an unsupported value",
    };

    private static PyValue Required(Dictionary<string, PyValue> fields, string key, string at) =>
        fields.TryGetValue(key, out var value) ? value : throw new InvalidDataException($"{at}.{key}: missing");

    private static IReadOnlyList<PyValue> Items(PyValue value, string at) =>
        value is PyList list ? list.Items : throw new InvalidDataException($"{at}: expected a list");

    private static List<string> Strs(Dictionary<string, PyValue> fields, string key, string at) =>
        fields.TryGetValue(key, out var value) ? [.. Items(value, $"{at}.{key}").Select((v, i) => Str(v, $"{at}.{key}.{i}"))] : [];

    private static string Str(PyValue value, string at) =>
        value is PyStr s ? s.Text : throw new InvalidDataException($"{at}: expected a string");

    private static string? OptionalStr(PyValue value, string at) => value is PyNone ? null : Str(value, at);

    // A YAML bool reads back as Python's True or False; nothing else renders that way.
    private static bool? Bool(PyValue value, string at) => value switch
    {
        PyNone => null,
        PyScalar { Rendered: "True" } => true,
        PyScalar { Rendered: "False" } => false,
        _ => throw new InvalidDataException($"{at}: expected a boolean"),
    };
}
