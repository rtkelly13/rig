using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Rig.Core.Vault;

/// <summary>A document PyYAML's safe loader would refuse; <see cref="Exception.Message"/> says why, in rig's words.</summary>
public sealed class PyYamlException : Exception
{
    /// <summary>A refusal with no reason given.</summary>
    public PyYamlException()
    {
    }

    /// <summary>A refusal for <paramref name="message"/>.</summary>
    public PyYamlException(string message)
        : base(message)
    {
    }

    /// <summary>A refusal for <paramref name="message"/>, caused by <paramref name="innerException"/>.</summary>
    public PyYamlException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <c>yaml.load(text, Loader=CSafeLoader)</c> as PyYAML 6 does it. YamlDotNet only parses; composing, YAML 1.1 implicit
/// tag resolution, merge keys and the safe constructors follow PyYAML's <c>composer</c>, <c>resolver</c> and
/// <c>constructor</c> modules, so a note's frontmatter reads the same here as in the Python tools it replaces.
/// </summary>
public static partial class PyYaml
{
    private const string Prefix = "tag:yaml.org,2002:";
    private const string StrTag = Prefix + "str";
    private const string SeqTag = Prefix + "seq";
    private const string MapTag = Prefix + "map";
    private const string MergeTag = Prefix + "merge";
    private const string ValueTag = Prefix + "value";

    /// <summary>
    /// The value of the single YAML document in <paramref name="text"/>, or <see langword="null"/> when it holds none.
    /// </summary>
    /// <exception cref="PyYamlException">PyYAML would raise on this text.</exception>
    public static PyValue? Load(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        RejectUnprintable(text);
        Node? root;
        try
        {
            root = Compose(text);
        }
        catch (YamlException exception)
        {
            throw new PyYamlException(exception.Message, exception);
        }

        return root is null ? null : new Constructor().Construct(root);
    }

    // libyaml's reader refuses any character outside YAML's printable set before it parses anything.
    private static void RejectUnprintable(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var printable = value is 0x09 or 0x0A or 0x0D or 0x85
                || value is >= 0x20 and <= 0x7E
                || value is >= 0xA0 and <= 0xD7FF
                || value is >= 0xE000 and <= 0xFFFD
                || value is >= 0x10000 and <= 0x10FFFF;
            if (!printable)
            {
                throw new PyYamlException($"special characters are not allowed (U+{value:X4})");
            }
        }
    }

    private static Node? Compose(string text)
    {
        var parser = new Parser(new StringReader(text));
        _ = parser.MoveNext();
        Expect<StreamStart>(parser);
        if (parser.Current is StreamEnd)
        {
            return null;
        }

        Expect<DocumentStart>(parser);
        var node = new Composer(parser).ComposeNode();
        Expect<DocumentEnd>(parser);
        if (parser.Current is not StreamEnd)
        {
            throw new PyYamlException("expected a single document in the stream");
        }

        return node;
    }

    private static void Expect<T>(Parser parser)
        where T : ParsingEvent
    {
        if (parser.Current is not T)
        {
            throw new PyYamlException($"expected {typeof(T).Name}, found {parser.Current?.GetType().Name}");
        }

        _ = parser.MoveNext();
    }

    private abstract class Node
    {
        public required string Tag { get; set; }
    }

    private sealed class ScalarNode : Node
    {
        public required string Value { get; init; }
    }

    private sealed class SequenceNode : Node
    {
        public List<Node> Items { get; } = [];
    }

    private sealed class MappingNode : Node
    {
        public List<KeyValuePair<Node, Node>> Pairs { get; } = [];
    }

    private sealed class Composer(Parser parser)
    {
        private readonly Dictionary<string, Node> _anchors = new(StringComparer.Ordinal);

        public Node ComposeNode()
        {
            var current = parser.Current ?? throw new PyYamlException("unexpected end of stream");
            if (current is AnchorAlias alias)
            {
                _ = parser.MoveNext();
                return _anchors.TryGetValue(alias.Value.Value, out var target)
                    ? target
                    : throw new PyYamlException($"found undefined alias {alias.Value.Value}");
            }

            return current switch
            {
                Scalar scalar => ComposeScalar(scalar),
                SequenceStart start => ComposeSequence(start),
                MappingStart start => ComposeMapping(start),
                _ => throw new PyYamlException($"unexpected {current.GetType().Name}"),
            };
        }

        private void Anchor(AnchorName anchor, Node node)
        {
            if (anchor.IsEmpty)
            {
                return;
            }

            if (!_anchors.TryAdd(anchor.Value, node))
            {
                throw new PyYamlException($"found duplicate anchor {anchor.Value}");
            }
        }

        private ScalarNode ComposeScalar(Scalar scalar)
        {
            var tag = scalar.Tag;
            var resolved = tag.IsEmpty || tag.IsNonSpecific
                ? Resolve(scalar.Value, scalar.Style == ScalarStyle.Plain || tag.IsNonSpecific)
                : tag.Value;
            var node = new ScalarNode { Tag = resolved, Value = scalar.Value };
            Anchor(scalar.Anchor, node);
            _ = parser.MoveNext();
            return node;
        }

        private SequenceNode ComposeSequence(SequenceStart start)
        {
            var node = new SequenceNode { Tag = start.Tag.IsEmpty || start.Tag.IsNonSpecific ? SeqTag : start.Tag.Value };
            Anchor(start.Anchor, node);
            _ = parser.MoveNext();
            while (parser.Current is not SequenceEnd)
            {
                node.Items.Add(ComposeNode());
            }

            _ = parser.MoveNext();
            return node;
        }

        private MappingNode ComposeMapping(MappingStart start)
        {
            var node = new MappingNode { Tag = start.Tag.IsEmpty || start.Tag.IsNonSpecific ? MapTag : start.Tag.Value };
            Anchor(start.Anchor, node);
            _ = parser.MoveNext();
            while (parser.Current is not MappingEnd)
            {
                var key = ComposeNode();
                node.Pairs.Add(new(key, ComposeNode()));
            }

            _ = parser.MoveNext();
            return node;
        }
    }

    // PyYAML's Resolver: YAML 1.1's implicit types, tried in the order PyYAML registers them.
    private static string Resolve(string value, bool plain)
    {
        if (!plain)
        {
            return StrTag;
        }

        if (value.Length == 0)
        {
            return Prefix + "null";
        }

        (string Tag, Regex Pattern)[] resolvers =
        [
            (Prefix + "bool", BoolPattern()),
            (Prefix + "float", FloatPattern()),
            (Prefix + "int", IntPattern()),
            (MergeTag, MergePattern()),
            (Prefix + "null", NullPattern()),
            (Prefix + "timestamp", TimestampPattern()),
            (ValueTag, ValuePattern()),
        ];
        foreach (var (tag, pattern) in resolvers)
        {
            if (pattern.IsMatch(value))
            {
                return tag;
            }
        }

        return StrTag;
    }

    // Python's `$` also matches before a final newline, which .NET's `\z` does not: `\n?\z` keeps that.
    [GeneratedRegex(@"^(?:yes|Yes|YES|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF)\n?\z")]
    private static partial Regex BoolPattern();

    [GeneratedRegex(@"^(?:[-+]?(?:[0-9][0-9_]*)\.[0-9_]*(?:[eE][-+][0-9]+)?|\.[0-9_]+(?:[eE][-+][0-9]+)?|[-+]?[0-9][0-9_]*(?::[0-5]?[0-9])+\.[0-9_]*|[-+]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))\n?\z")]
    private static partial Regex FloatPattern();

    [GeneratedRegex(@"^(?:[-+]?0b[0-1_]+|[-+]?0[0-7_]+|[-+]?(?:0|[1-9][0-9_]*)|[-+]?0x[0-9a-fA-F_]+|[-+]?[1-9][0-9_]*(?::[0-5]?[0-9])+)\n?\z")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^(?:<<)\n?\z")]
    private static partial Regex MergePattern();

    [GeneratedRegex(@"^(?:~|null|Null|NULL|)\n?\z")]
    private static partial Regex NullPattern();

    [GeneratedRegex(@"^(?:[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]|[0-9][0-9][0-9][0-9]-[0-9][0-9]?-[0-9][0-9]?(?:[Tt]|[ \t]+)[0-9][0-9]?:[0-9][0-9]:[0-9][0-9](?:\.[0-9]*)?(?:[ \t]*(?:Z|[-+][0-9][0-9]?(?::[0-9][0-9])?))?)\n?\z")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"^(?:=)\n?\z")]
    private static partial Regex ValuePattern();

    [GeneratedRegex(@"^(?<year>[0-9][0-9][0-9][0-9])-(?<month>[0-9][0-9]?)-(?<day>[0-9][0-9]?)(?:(?:[Tt]|[ \t]+)(?<hour>[0-9][0-9]?):(?<minute>[0-9][0-9]):(?<second>[0-9][0-9])(?:\.(?<fraction>[0-9]*))?(?:[ \t]*(?<tz>Z|(?<sign>[-+])(?<tzhour>[0-9][0-9]?)(?::(?<tzminute>[0-9][0-9]))?))?)?\n?\z")]
    private static partial Regex TimestampParts();

    // PyYAML's SafeConstructor. A node reached twice through an alias builds one value; a collection that holds itself is
    // legal, since PyYAML hands out the collection before filling it, and here reads as an empty one.
    private sealed class Constructor
    {
        private readonly Dictionary<Node, PyValue> _built = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Node> _building = new(ReferenceEqualityComparer.Instance);

        public PyValue Construct(Node node)
        {
            if (_built.TryGetValue(node, out var value))
            {
                return value;
            }

            if (!_building.Add(node))
            {
                return node is MappingNode { Tag: MapTag } ? new PyDict([]) : node is MappingNode ? PyOther.Value : new PyList([]);
            }

            value = node switch
            {
                ScalarNode scalar => ConstructScalar(scalar),
                SequenceNode sequence => ConstructSequence(sequence),
                MappingNode mapping => ConstructMapping(mapping),
                _ => throw new PyYamlException("unknown node"),
            };
            _ = _building.Remove(node);
            _built[node] = value;
            return value;
        }

        private static PyValue ConstructScalar(ScalarNode node) => node.Tag switch
        {
            Prefix + "null" => PyNone.Value,
            Prefix + "bool" => new PyScalar(ConstructBool(node.Value)),
            Prefix + "int" => new PyScalar(ConstructInt(node.Value).ToString(CultureInfo.InvariantCulture)),
            Prefix + "float" => new PyScalar(PyFloat.Repr(ConstructFloat(node.Value))),
            Prefix + "binary" => ConstructBinary(node.Value),
            Prefix + "timestamp" => new PyScalar(ConstructTimestamp(node.Value)),
            StrTag => new PyStr(node.Value),
            _ => throw new PyYamlException($"could not determine a constructor for the tag {node.Tag}"),
        };

        private PyList ConstructSequence(SequenceNode node) => node.Tag switch
        {
            SeqTag => new PyList([.. node.Items.Select(Construct)]),
            Prefix + "omap" or Prefix + "pairs" => ConstructPairs(node),
            _ => throw new PyYamlException($"could not construct {node.Tag} from a sequence"),
        };

        // !!omap and !!pairs: a list of one-entry mappings, built as a list of tuples.
        private PyList ConstructPairs(SequenceNode node)
        {
            foreach (var item in node.Items)
            {
                if (item is not MappingNode { Pairs.Count: 1 } pair)
                {
                    throw new PyYamlException("expected a mapping of length 1");
                }

                _ = Construct(pair.Pairs[0].Key);
                _ = Construct(pair.Pairs[0].Value);
            }

            return new PyList([.. node.Items.Select(_ => (PyValue)PyOther.Value)]);
        }

        private PyValue ConstructMapping(MappingNode node)
        {
            if (node.Tag is not (MapTag or Prefix + "set"))
            {
                throw new PyYamlException($"could not construct {node.Tag} from a mapping");
            }

            var entries = new List<KeyValuePair<PyValue, PyValue>>();
            var index = new Dictionary<PyValue, int>();
            foreach (var (keyNode, valueNode) in Flatten(node))
            {
                var key = Construct(keyNode);
                if (keyNode is not ScalarNode)
                {
                    throw new PyYamlException("found unhashable key");
                }

                var value = Construct(valueNode);
                if (index.TryGetValue(key, out var at))
                {
                    entries[at] = new(entries[at].Key, value);
                }
                else
                {
                    index[key] = entries.Count;
                    entries.Add(new(key, value));
                }
            }

            return node.Tag == MapTag ? new PyDict(entries) : PyOther.Value;
        }

        // SafeConstructor.flatten_mapping: merged entries go first, so the mapping's own keys win.
        private static List<KeyValuePair<Node, Node>> Flatten(MappingNode node)
        {
            var merge = new List<KeyValuePair<Node, Node>>();
            var own = new List<KeyValuePair<Node, Node>>();
            foreach (var pair in node.Pairs)
            {
                if (pair.Key.Tag != MergeTag)
                {
                    if (pair.Key.Tag == ValueTag)
                    {
                        pair.Key.Tag = StrTag;
                    }

                    own.Add(pair);
                    continue;
                }

                switch (pair.Value)
                {
                    case MappingNode mapping:
                        merge.AddRange(Flatten(mapping));
                        break;
                    case SequenceNode sequence:
                        var parts = sequence.Items
                            .Select(item => item is MappingNode part
                                ? Flatten(part)
                                : throw new PyYamlException("expected a mapping for merging"))
                            .ToList();
                        parts.Reverse();
                        merge.AddRange(parts.SelectMany(part => part));
                        break;
                    default:
                        throw new PyYamlException("expected a mapping or list of mappings for merging");
                }
            }

            merge.AddRange(own);
            return merge;
        }

        private static string ConstructBool(string value) => value.ToLowerInvariant() switch
        {
            "yes" or "true" or "on" => "True",
            "no" or "false" or "off" => "False",
            _ => throw new PyYamlException($"not a bool: {value}"),
        };

        private static BigInteger ConstructInt(string text)
        {
            var value = text.Replace("_", string.Empty, StringComparison.Ordinal);
            if (value.Length == 0)
            {
                throw new PyYamlException("empty int");
            }

            var sign = value[0] == '-' ? BigInteger.MinusOne : BigInteger.One;
            if (value[0] is '+' or '-')
            {
                value = value[1..];
            }

            if (value == "0")
            {
                return BigInteger.Zero;
            }

            if (value.StartsWith("0b", StringComparison.Ordinal))
            {
                return sign * PyInt.Parse(value[2..], 2);
            }

            if (value.StartsWith("0x", StringComparison.Ordinal))
            {
                return sign * PyInt.Parse(value[2..], 16);
            }

            if (value.StartsWith('0'))
            {
                return sign * PyInt.Parse(value, 8);
            }

            if (value.Contains(':', StringComparison.Ordinal))
            {
                return sign * Sexagesimal(value.Split(':').Select(part => PyInt.Parse(part, 10)));
            }

            return sign * PyInt.Parse(value, 10);
        }

        private static BigInteger Sexagesimal(IEnumerable<BigInteger> digits) =>
            digits.Aggregate(BigInteger.Zero, (total, digit) => (total * 60) + digit);

        private static double ConstructFloat(string text)
        {
            var value = text.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (value.Length == 0)
            {
                throw new PyYamlException("empty float");
            }

            var sign = value[0] == '-' ? -1.0 : 1.0;
            if (value[0] is '+' or '-')
            {
                value = value[1..];
            }

            if (value == ".inf")
            {
                return sign * double.PositiveInfinity;
            }

            if (value == ".nan")
            {
                return double.NaN;
            }

            if (value.Contains(':', StringComparison.Ordinal))
            {
                return sign * value.Split(':').Select(PyFloat.Parse).Aggregate(0.0, (total, digit) => (total * 60) + digit);
            }

            return sign * PyFloat.Parse(value);
        }

        private static PyOther ConstructBinary(string value)
        {
            if (!value.All(char.IsAscii))
            {
                throw new PyYamlException("failed to convert base64 data into ascii");
            }

            // base64.decodebytes skips anything outside the alphabet, then needs whole quads.
            var data = new string([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')]);
            try
            {
                _ = Convert.FromBase64String(data);
            }
            catch (FormatException exception)
            {
                throw new PyYamlException("failed to decode base64 data", exception);
            }

            return PyOther.Value;
        }

        private static string ConstructTimestamp(string value)
        {
            var match = TimestampParts().Match(value);
            if (!match.Success)
            {
                throw new PyYamlException($"not a timestamp: {value}");
            }

            int Part(string name) => int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture);
            DateOnly date;
            try
            {
                date = new DateOnly(Part("year"), Part("month"), Part("day"));
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new PyYamlException("day is out of range for month", exception);
            }

            var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!match.Groups["hour"].Success)
            {
                return day;
            }

            var (hour, minute, second) = (Part("hour"), Part("minute"), Part("second"));
            if (hour > 23 || minute > 59 || second > 59)
            {
                throw new PyYamlException("time is out of range");
            }

            var fraction = match.Groups["fraction"].Value;
            fraction = fraction.Length > 6 ? fraction[..6] : fraction.PadRight(6, '0');
            var micro = int.Parse(fraction, CultureInfo.InvariantCulture);
            var text = $"{day} {hour:00}:{minute:00}:{second:00}" + (micro == 0 ? string.Empty : $".{micro:000000}");
            if (match.Groups["sign"].Success)
            {
                var offset = (Part("tzhour") * 60) + (match.Groups["tzminute"].Success ? Part("tzminute") : 0);
                if (offset >= 24 * 60)
                {
                    throw new PyYamlException("offset must be a timedelta strictly between -timedelta(hours=24) and timedelta(hours=24)");
                }

                var sign = offset == 0 || match.Groups["sign"].Value == "+" ? '+' : '-';
                return $"{text}{sign}{offset / 60:00}:{offset % 60:00}";
            }

            return match.Groups["tz"].Success ? $"{text}+00:00" : text;
        }
    }
}
