using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Rig.Core.Vault;

/// <summary>A document the loader refuses; <see cref="Exception.Message"/> says why, in rig's words.</summary>
public sealed class VaultYamlException : Exception
{
    /// <summary>A refusal with no reason given.</summary>
    public VaultYamlException()
    {
    }

    /// <summary>A refusal for <paramref name="message"/>.</summary>
    public VaultYamlException(string message)
        : base(message)
    {
    }

    /// <summary>A refusal for <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public VaultYamlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Loads one YAML document into <see cref="YamlValue"/>s. YamlDotNet's parser supplies the events; this builds the values: anchors,
/// merge keys and the scalar types frontmatter uses (null, <c>yes</c>/<c>no</c>/<c>on</c>/<c>off</c> booleans, integers, floats and
/// ISO dates), so a note written by hand keeps its meaning. Anything else plain is a string.
/// </summary>
public static partial class VaultYaml
{
    private const string StrTag = "tag:yaml.org,2002:str";
    private const string SeqTag = "tag:yaml.org,2002:seq";
    private const string MapTag = "tag:yaml.org,2002:map";

    /// <summary>The value of the single YAML document in <paramref name="text"/>, or <see langword="null"/> when it holds none.</summary>
    /// <exception cref="VaultYamlException">The text is not a loadable YAML document.</exception>
    public static YamlValue? Load(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var parser = new Parser(new StringReader(text));
            _ = parser.MoveNext();
            _ = parser.MoveNext();
            if (parser.Current is StreamEnd)
            {
                return null;
            }

            _ = parser.MoveNext();
            var value = Build(parser, new Dictionary<string, YamlValue>(StringComparer.Ordinal));
            _ = parser.MoveNext();
            _ = parser.MoveNext();
            return parser.Current is StreamEnd ? value : throw new VaultYamlException("expected a single document in the stream");
        }
        catch (YamlException exception)
        {
            throw new VaultYamlException(exception.Message, exception);
        }
    }

    // Reads the node the parser stands on and leaves it on the node's last event.
    private static YamlValue Build(Parser parser, Dictionary<string, YamlValue> anchors)
    {
        var start = parser.Current ?? throw new VaultYamlException("unexpected end of stream");
        if (start is AnchorAlias alias)
        {
            return anchors.TryGetValue(alias.Value.Value, out var known) ? known : throw new VaultYamlException($"unknown anchor '{alias.Value}'");
        }

        var (anchor, tag) = start switch
        {
            Scalar scalar => (scalar.Anchor, scalar.Tag),
            SequenceStart sequence => (sequence.Anchor, sequence.Tag),
            MappingStart mapping => (mapping.Anchor, mapping.Tag),
            _ => throw new VaultYamlException($"unexpected {start.GetType().Name}"),
        };
        var value = start switch
        {
            Scalar scalar => Resolve(scalar),
            SequenceStart => CheckTag(tag, SeqTag, new YamlList(Items(parser, anchors))),
            _ => CheckTag(tag, MapTag, new YamlMap(Entries(parser, anchors))),
        };
        if (!anchor.IsEmpty)
        {
            anchors[anchor.Value] = value;
        }

        return value;
    }

    private static YamlValue CheckTag(TagName tag, string expected, YamlValue value) =>
        tag.IsEmpty || tag.Value == expected ? value : throw new VaultYamlException($"unsupported tag {tag.Value}");

    private static List<YamlValue> Items(Parser parser, Dictionary<string, YamlValue> anchors)
    {
        var items = new List<YamlValue>();
        while (parser.MoveNext() && parser.Current is not SequenceEnd)
        {
            items.Add(Build(parser, anchors));
        }

        return items;
    }

    // Keys stay unique, a repeated one keeping its first place and its last value; `<<` merges the mappings it names in
    // behind the keys written out.
    private static List<KeyValuePair<YamlValue, YamlValue>> Entries(Parser parser, Dictionary<string, YamlValue> anchors)
    {
        var entries = new List<KeyValuePair<YamlValue, YamlValue>>();
        var merged = new List<YamlMap>();
        while (parser.MoveNext() && parser.Current is not MappingEnd)
        {
            var key = Build(parser, anchors);
            if (key is YamlList or YamlMap)
            {
                throw new VaultYamlException("found unhashable key");
            }

            _ = parser.MoveNext();
            var value = Build(parser, anchors);
            if (key == new YamlString("<<"))
            {
                merged.AddRange(value switch { YamlMap map => [map], YamlList list => list.Items.OfType<YamlMap>(), _ => [] });
                continue;
            }

            Put(entries, key, value, replace: true);
        }

        foreach (var entry in merged.SelectMany(map => map.Entries))
        {
            Put(entries, entry.Key, entry.Value, replace: false);
        }

        return entries;
    }

    private static string Where(Mark at) => $"line {at.Line}, column {at.Column}";

    private static void Put(List<KeyValuePair<YamlValue, YamlValue>> entries, YamlValue key, YamlValue value, bool replace)
    {
        var at = entries.FindIndex(entry => entry.Key.Equals(key));
        if (at < 0)
        {
            entries.Add(new(key, value));
        }
        else if (replace)
        {
            entries[at] = new(key, value);
        }
    }

    private static YamlValue Resolve(Scalar scalar)
    {
        if (!scalar.Tag.IsEmpty)
        {
            return scalar.Tag.Value == StrTag ? new YamlString(scalar.Value) : throw new VaultYamlException($"unsupported tag {scalar.Tag.Value}");
        }

        var text = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return new YamlString(text);
        }

        if (text is "" or "~" or "null" or "Null" or "NULL")
        {
            return YamlNull.Value;
        }

        if (Bool().IsMatch(text))
        {
            return new YamlScalar(text.ToUpperInvariant() is "YES" or "TRUE" or "ON" ? "True" : "False");
        }

        if (Hex().IsMatch(text) || Integer().IsMatch(text))
        {
            return new YamlScalar(ParseInteger(text).ToString(CultureInfo.InvariantCulture));
        }

        if (Real().IsMatch(text))
        {
            return double.TryParse(text.Replace("_", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                ? new YamlScalar(YamlFloat.Repr(real))
                : throw new VaultYamlException($"malformed number '{text}' at {Where(scalar.Start)}");
        }

        if (Infinity().IsMatch(text))
        {
            return new YamlScalar(text[0] == '-' ? "-inf" : "inf");
        }

        if (text is ".nan" or ".NaN" or ".NAN")
        {
            return new YamlScalar("nan");
        }

        return IsDate(text) ? new YamlScalar(text) : new YamlString(text);
    }

    private static BigInteger ParseInteger(string text)
    {
        var digits = text.Replace("_", string.Empty, StringComparison.Ordinal);
        var negative = digits[0] == '-';
        digits = digits.TrimStart('+', '-');
        var magnitude = digits.StartsWith("0x", StringComparison.Ordinal)
            ? BigInteger.Parse("0" + digits[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        return negative ? -magnitude : magnitude;
    }

    private static bool IsDate(string text) => DateText().IsMatch(text) && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex("^(?:yes|no|true|false|on|off)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bool();

    [GeneratedRegex(@"^[-+]?(?:0|[1-9][0-9_]*)$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^[-+]?0x[0-9a-fA-F_]+$")]
    private static partial Regex Hex();

    [GeneratedRegex(@"^[-+]?(?:[0-9][0-9_]*\.[0-9_]*|\.[0-9_]+)(?:[eE][-+][0-9]+)?$")]
    private static partial Regex Real();

    [GeneratedRegex(@"^[-+]?\.(?:inf|Inf|INF)$")]
    private static partial Regex Infinity();

    [GeneratedRegex(@"^\d{4}-\d\d-\d\d(?:(?:[Tt]|[ \t]+)\d\d?:\d\d:\d\d(?:\.\d*)?(?:[ \t]*(?:Z|[-+]\d\d?(?::\d\d)?))?)?$")]
    private static partial Regex DateText();
}
