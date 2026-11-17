using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rig.Core.Vault;

/// <summary>A note's frontmatter: its metadata, or why the note could not be read.</summary>
/// <param name="Metadata">Each top-level key and its value; empty when the note has none or could not be read.</param>
/// <param name="Error">Why the note could not be read, or <see langword="null"/>.</param>
public sealed record FrontmatterRead(IReadOnlyDictionary<string, YamlValue> Metadata, string? Error)
{
    /// <summary>The value under <paramref name="key"/>, or <see langword="null"/> when the key is absent.</summary>
    public YamlValue? Get(string key) => Metadata.GetValueOrDefault(key);

    /// <summary>The string under <paramref name="key"/>, or <see langword="null"/> when it is absent or not a string.</summary>
    public string? Text(string key) => Get(key) is YamlString text ? text.Text : null;
}

/// <summary>
/// A note's metadata: the file read as UTF-8 text with universal
/// newlines, stripped, split on its first two <c>---</c> rules and loaded with <see cref="VaultYaml"/>. Its JSON handler
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

    /// <summary>A note's text as text: strict UTF-8, with universal newlines.</summary>
    /// <exception cref="DecoderFallbackException"><paramref name="raw"/> is not UTF-8.</exception>
    public static string Decode(ReadOnlySpan<byte> raw) =>
        Strict.GetString(raw).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>The frontmatter of a note whose text, already decoded with universal newlines, is <paramref name="text"/>.</summary>
    public static FrontmatterRead Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripped = UnicodeText.Strip(text);
        try
        {
            if (YamlBoundary().Match(stripped) is { Success: true, Index: 0 })
            {
                return Split(stripped, YamlBoundary()) is { } yaml ? Metadata(VaultYaml.Load(yaml)) : Empty;
            }

            if (JsonBoundary().Match(stripped) is { Success: true, Index: 0 })
            {
                return Split(stripped, JsonBoundary()) is { } json ? Metadata(LoadJson("{" + json + "}")) : Empty;
            }
        }
        catch (VaultYamlException exception)
        {
            return Refused(exception.Message);
        }

        return Empty;
    }

    /// <summary>
    /// What follows the first two rules, stripped, or the whole text stripped when
    /// there is no frontmatter.
    /// </summary>
    public static string Content(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stripped = UnicodeText.Strip(text);
        foreach (var boundary in new[] { YamlBoundary(), JsonBoundary() })
        {
            if (boundary.Match(stripped) is { Success: true, Index: 0 } first)
            {
                var second = first.NextMatch();
                return second.Success ? UnicodeText.Strip(stripped[(second.Index + second.Length)..]) : stripped;
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
        while (start < text.Length && UnicodeText.IsSpace(text[start]))
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

    private static FrontmatterRead Empty { get; } = new(new Dictionary<string, YamlValue>(), null);

    private static FrontmatterRead Refused(string reason) => new(new Dictionary<string, YamlValue>(), reason);

    // The text between the first two rules, or null when there are fewer.
    private static string? Split(string text, Regex boundary)
    {
        var first = boundary.Match(text);
        var second = first.NextMatch();
        return second.Success ? text[(first.Index + first.Length)..second.Index] : null;
    }

    // frontmatter keeps a mapping and drops anything else, then spreads it into Post(content, handler, **metadata).
    private static FrontmatterRead Metadata(YamlValue? loaded)
    {
        if (loaded is not YamlMap dict)
        {
            return Empty;
        }

        var metadata = new Dictionary<string, YamlValue>(StringComparer.Ordinal);
        foreach (var (key, value) in dict.Entries)
        {
            if (key is not YamlString { Text: var name })
            {
                return Refused("keywords must be strings");
            }

            if (name is "content" or "handler")
            {
                return Refused($"the key '{name}' is reserved");
            }

            metadata[name] = value;
        }

        return new(metadata, null);
    }

    private static YamlValue LoadJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return FromJson(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new VaultYamlException(exception.Message, exception);
        }
    }

    private static YamlValue FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => new YamlString(element.GetString() ?? string.Empty),
        JsonValueKind.Null => YamlNull.Value,
        JsonValueKind.True => new YamlScalar("True"),
        JsonValueKind.False => new YamlScalar("False"),
        JsonValueKind.Number => new YamlScalar(JsonNumber(element.GetRawText())),
        JsonValueKind.Array => new YamlList([.. element.EnumerateArray().Select(FromJson)]),
        _ => new YamlMap(JsonEntries(element)),
    };

    // Of repeated keys the last value wins, at the position of the first.
    private static List<KeyValuePair<YamlValue, YamlValue>> JsonEntries(JsonElement element)
    {
        var entries = new List<KeyValuePair<YamlValue, YamlValue>>();
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
            entries.Add(new(new YamlString(property.Name), value));
        }

        return entries;
    }

    private static string JsonNumber(string raw) => raw.IndexOfAny(['.', 'e', 'E']) < 0
        ? System.Numerics.BigInteger.Parse(raw, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
        : YamlFloat.Repr(double.Parse(raw, CultureInfo.InvariantCulture));

    // A frontmatter boundary: `^-{3,}\s*$` per line, with `\s` including U+001C to U+001F.
    [GeneratedRegex(@"^-{3,}[" + UnicodeText.Space + @"]*$", RegexOptions.Multiline)]
    private static partial Regex YamlBoundary();

    // JSONHandler.FM_BOUNDARY, `^(?:{|})$` under re.MULTILINE.
    [GeneratedRegex(@"^(?:\{|\})$", RegexOptions.Multiline)]
    private static partial Regex JsonBoundary();
}
