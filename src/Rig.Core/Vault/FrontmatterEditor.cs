using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>A note's text after a frontmatter edit, or why the edit was refused.</summary>
/// <param name="Text">The edited text, or <see langword="null"/> when the edit was refused.</param>
/// <param name="Refusal">Why the edit was refused, or <see langword="null"/>.</param>
public sealed record FrontmatterEdit(string? Text, string? Refusal);

/// <summary>
/// Sets top-level frontmatter keys to strings by rewriting only those keys' lines. The Python re-dumped the whole block
/// through PyYAML, which sorted the keys, restyled every value and stripped the body; this leaves everything else as it
/// was. Each edit is proved by reading it back: the edited note must load to the old metadata with exactly the changes
/// applied and the same content, or the edit is refused.
/// </summary>
public static class FrontmatterEditor
{
    /// <summary>The note <paramref name="text"/> with each of <paramref name="changes"/> set, in order.</summary>
    /// <param name="text">The note, decoded with universal newlines.</param>
    /// <param name="changes">Top-level keys and the strings to set them to; a key set twice takes its last value.</param>
    public static FrontmatterEdit Set(string text, IReadOnlyList<KeyValuePair<string, string>> changes)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(changes);
        var before = Frontmatter.Parse(text);
        if (before.Error is { } error)
        {
            return new(null, $"Invalid frontmatter: {error}");
        }

        string edited;
        if (Frontmatter.YamlBlock(text) is { } block)
        {
            var (offset, length) = block.GetOffsetAndLength(text.Length);
            var yaml = text.Substring(offset, length);
            foreach (var (key, value) in changes)
            {
                yaml = SetLine(yaml, key, value);
            }

            edited = string.Concat(text.AsSpan(0, offset), yaml, text.AsSpan(offset + length));
        }
        else
        {
            var yaml = new StringBuilder();
            foreach (var (key, value) in changes.GroupBy(c => c.Key, StringComparer.Ordinal).Select(g => g.Last()))
            {
                yaml.Append(Line(key, value));
            }

            edited = $"---\n{yaml}---\n\n{text}";
        }

        var expected = new Dictionary<string, PyValue>(before.Metadata, StringComparer.Ordinal);
        foreach (var (key, value) in changes)
        {
            expected[key] = new PyStr(value);
        }

        var after = Frontmatter.Parse(edited);
        return after.Error is null && SameMetadata(after.Metadata, expected) && Frontmatter.Content(edited) == Frontmatter.Content(text)
            ? new(edited, null)
            : new(null, "its frontmatter could not be edited line by line and read back as intended");
    }

    /// <summary>
    /// <paramref name="value"/> as a YAML scalar: plain when PyYAML reads that back as the same string, otherwise double
    /// quoted. A <c>source</c> is always quoted, as the Python forced.
    /// </summary>
    public static string Scalar(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        return key != "source" && ReadsBackPlain(value) ? value : Quote(value);
    }

    /// <summary>Whether two loaded metadata mappings hold the same keys and values.</summary>
    public static bool SameMetadata(IReadOnlyDictionary<string, PyValue> left, IReadOnlyDictionary<string, PyValue> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var other) && Same(pair.Value, other));
    }

    private static string Line(string key, string value) => $"{key}: {Scalar(key, value)}\n";

    // Replaces the last line that opens `key:` at the margin, with the indented or `- ` lines that continue its value;
    // appends the key when no line opens it.
    private static string SetLine(string yaml, string key, string value)
    {
        var lines = new List<string>();
        for (var start = 0; start < yaml.Length;)
        {
            var end = yaml.IndexOf('\n', start);
            end = end < 0 ? yaml.Length : end + 1;
            lines.Add(yaml[start..end]);
            start = end;
        }

        var opens = new Regex("^" + Regex.Escape(key) + @"[ \t]*:(?:[ \t\n]|$)", RegexOptions.CultureInvariant);
        var at = lines.FindLastIndex(opens.IsMatch);
        if (at < 0)
        {
            return (yaml.EndsWith('\n') ? yaml : yaml + "\n") + Line(key, value);
        }

        var last = at;
        for (var i = at + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            if (!Continues(lines[i]))
            {
                break;
            }

            last = i;
        }

        lines.RemoveRange(at, last - at + 1);
        lines.Insert(at, Line(key, value));
        return string.Concat(lines);
    }

    private static bool Continues(string line) =>
        line[0] is ' ' or '\t' || line is "-" or "-\n" || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("-\t", StringComparison.Ordinal);

    private static bool ReadsBackPlain(string value)
    {
        try
        {
            return PyYaml.Load($"k: {value}\n") is PyDict { Entries: [{ Key: PyStr { Text: "k" }, Value: PyStr { Text: var text } }] } && text == value;
        }
        catch (PyYamlException)
        {
            return false;
        }
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                // Whatever PyYAML's reader refuses or reads as a line break, escaped.
                case < 0x20 or 0x7F or (>= 0x80 and <= 0x9F) or 0x2028 or 0x2029 or 0xFEFF or 0xFFFE or 0xFFFF:
                    quoted.Append("\\u").Append(rune.Value.ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    quoted.Append(rune.ToString());
                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    private static bool Same(PyValue left, PyValue right) => (left, right) switch
    {
        (PyStr a, PyStr b) => a.Text == b.Text,
        (PyScalar a, PyScalar b) => a.Rendered == b.Rendered,
        (PyNone, PyNone) or (PyOther, PyOther) => true,
        (PyList a, PyList b) => a.Items.Count == b.Items.Count && a.Items.Zip(b.Items).All(p => Same(p.First, p.Second)),
        (PyDict a, PyDict b) => a.Entries.Count == b.Entries.Count
            && a.Entries.Zip(b.Entries).All(p => Same(p.First.Key, p.Second.Key) && Same(p.First.Value, p.Second.Value)),
        _ => false,
    };
}
