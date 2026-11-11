using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>A note's frontmatter: its metadata, or why python-frontmatter would have refused the note.</summary>
/// <param name="Metadata">Each top-level key and its value; empty when the note has none or could not be read.</param>
/// <param name="Error">Why the note could not be read, or <see langword="null"/>.</param>
public sealed record FrontmatterRead(IReadOnlyDictionary<string, PyValue> Metadata, string? Error)
{
    /// <summary>The value under <paramref name="key"/>, or <see langword="null"/> when the key is absent.</summary>
    public PyValue? Get(string key) => Metadata.GetValueOrDefault(key);

    /// <summary>The string under <paramref name="key"/>, or <see langword="null"/> when it is absent or not a string.</summary>
    public string? Text(string key) => Get(key) is PyStr text ? text.Text : null;
}

/// <summary>
/// <c>frontmatter.load(path).metadata</c> as python-frontmatter 1.x does it: the file read as UTF-8 text with universal
/// newlines, stripped, split on its first two <c>---</c> rules and loaded with PyYAML's safe loader. Its JSON handler
/// (a block opened by a lone <c>{</c>) is honoured too; TOML needs a package rig never installed, so <c>+++</c> is body.
/// </summary>
public static partial class Frontmatter
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The frontmatter of the note at <paramref name="path"/>.</summary>
    public static FrontmatterRead Load(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (IOException exception)
        {
            return Refused(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Refused(exception.Message);
        }
    }

    /// <summary>The frontmatter of a note whose file holds <paramref name="raw"/>.</summary>
    public static FrontmatterRead Read(ReadOnlySpan<byte> raw)
    {
        try
        {
            return Parse(Decode(raw));
        }
        catch (DecoderFallbackException exception)
        {
            return Refused($"'utf-8' codec can't decode the note: {exception.Message}");
        }
    }

    /// <summary>A note's text as Python's text mode reads it: strict UTF-8, with universal newlines.</summary>
    /// <exception cref="DecoderFallbackException"><paramref name="raw"/> is not UTF-8.</exception>
    public static string Decode(ReadOnlySpan<byte> raw) =>
        Strict.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>The frontmatter of a note whose text, already decoded with universal newlines, is <paramref name="text"/>.</summary>
    public static FrontmatterRead Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripped = PyText.Strip(text);
        try
        {
            if (YamlBoundary().Match(stripped) is { Success: true, Index: 0 })
            {
                return Split(stripped, YamlBoundary()) is { } yaml ? Metadata(PyYaml.Load(yaml)) : Empty;
            }

            if (JsonBoundary().Match(stripped) is { Success: true, Index: 0 })
            {
                return Split(stripped, JsonBoundary()) is { } json ? Metadata(LoadJson("{" + json + "}")) : Empty;
            }
        }
        catch (PyYamlException exception)
        {
            return Refused(exception.Message);
        }

        return Empty;
    }

    /// <summary>
    /// <c>frontmatter.loads(text).content</c>: what follows the first two rules, stripped, or the whole text stripped when
    /// there is no frontmatter.
    /// </summary>
    public static string Content(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripped = PyText.Strip(text);
        foreach (var boundary in new[] { YamlBoundary(), JsonBoundary() })
        {
            if (boundary.Match(stripped) is { Success: true, Index: 0 } first)
            {
                var second = first.NextMatch();
                return second.Success ? PyText.Strip(stripped[(second.Index + second.Length)..]) : stripped;
            }
        }

        return stripped;
    }

    /// <summary>
    /// Where the YAML between a note's first two <c>---</c> rules sits in <paramref name="text"/>, found as
    /// <see cref="Parse"/> finds it, or <see langword="null"/> when the note opens with no YAML block.
    /// </summary>
    internal static Range? YamlBlock(string text)
    {
        var start = 0;
        while (start < text.Length && PyText.IsSpace(text[start]))
        {
            start++;
        }

        // Matched from the first non-space character, as Parse matches the stripped text, so `^` holds there.
        var first = YamlBoundary().Match(text[start..]);
        if (first is not { Success: true, Index: 0 })
        {
            return null;
        }

        var second = first.NextMatch();
        return second.Success ? (start + first.Length)..(start + second.Index) : null;
    }

    private static FrontmatterRead Empty { get; } = new(new Dictionary<string, PyValue>(), null);

    private static FrontmatterRead Refused(string reason) => new(new Dictionary<string, PyValue>(), reason);

    // `re.split(text, 2)` unpacked into three parts: the text between the first two rules, or null when there are fewer.
    private static string? Split(string text, Regex boundary)
    {
        var first = boundary.Match(text);
        var second = first.NextMatch();
        return second.Success ? text[(first.Index + first.Length)..second.Index] : null;
    }

    // frontmatter keeps a mapping and drops anything else, then spreads it into Post(content, handler, **metadata).
    private static FrontmatterRead Metadata(PyValue? loaded)
    {
        if (loaded is not PyDict dict)
        {
            return Empty;
        }

        var metadata = new Dictionary<string, PyValue>(StringComparer.Ordinal);
        foreach (var (key, value) in dict.Entries)
        {
            if (key is not PyStr { Text: var name })
            {
                return Refused("keywords must be strings");
            }

            if (name is "content" or "handler")
            {
                return Refused($"Post.__init__() got multiple values for argument '{name}'");
            }

            metadata[name] = value;
        }

        return new(metadata, null);
    }

    private static PyValue LoadJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return FromJson(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new PyYamlException(exception.Message, exception);
        }
    }

    private static PyValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new PyStr(element.GetString() ?? string.Empty),
        JsonValueKind.Null => PyNone.Value,
        JsonValueKind.True => new PyScalar("True"),
        JsonValueKind.False => new PyScalar("False"),
        JsonValueKind.Number => new PyScalar(JsonNumber(element.GetRawText())),
        JsonValueKind.Array => new PyList([.. element.EnumerateArray().Select(FromJson)]),
        _ => new PyDict(JsonEntries(element)),
    };

    // json.loads keeps the last of repeated keys, where it first appeared.
    private static List<KeyValuePair<PyValue, PyValue>> JsonEntries(JsonElement element)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var value = FromJson(property.Value);
            if (index.TryGetValue(property.Name, out var at))
            {
                entries[at] = new(entries[at].Key, value);
                continue;
            }

            index[property.Name] = entries.Count;
            entries.Add(new(new PyStr(property.Name), value));
        }

        return entries;
    }

    private static string JsonNumber(string raw) => raw.IndexOfAny(['.', 'e', 'E']) < 0
        ? System.Numerics.BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
        : PyFloat.Repr(double.Parse(raw, CultureInfo.InvariantCulture));

    // YAMLHandler.FM_BOUNDARY, `^-{3,}\s*$` under re.MULTILINE, with Python's `\s`.
    [GeneratedRegex(@"^-{3,}[" + PyText.Space + @"]*$", RegexOptions.Multiline)]
    private static partial Regex YamlBoundary();

    // JSONHandler.FM_BOUNDARY, `^(?:{|})$` under re.MULTILINE.
    [GeneratedRegex(@"^(?:\{|\})$", RegexOptions.Multiline)]
    private static partial Regex JsonBoundary();
}
