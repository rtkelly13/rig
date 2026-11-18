using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// A frozen recorded baseline whose generator is gone; it holds the C# frontmatter reader to YAML 1.1 as PyYAML read it. <c>frontmatter-oracle.json</c> records,
/// for each note's raw bytes, whether it was refused and otherwise the kind of every key the lint reads: YAML 1.1's
/// implicit types, tags, merge keys, duplicate keys, the <c>---</c> boundary's quirks, BOMs, CRLF and bad UTF-8. Set
/// <c>RIG_FRONTMATTER_ORACLE</c> to a second fixture of the same shape, such as one recorded from a real vault.
/// </summary>
public sealed class FrontmatterOracleTests
{
    private const string ExtraFixtureVariable = "RIG_FRONTMATTER_ORACLE";

    private static readonly string[] Fields =
        ["type", "title", "source", "url", "related_article", "lint_ignore", "lint-ignore", "status", "created"];

    [Fact]
    public void Reads_every_recorded_note_as_recorded()
    {
        var extra = Environment.GetEnvironmentVariable(ExtraFixtureVariable);
        List<OracleCase> cases = [.. Load(Path.Combine(AppContext.BaseDirectory, "Vault", "frontmatter-oracle.json")), .. string.IsNullOrEmpty(extra) ? [] : Load(extra)];

        var mismatches = cases
            .Select(c => (Case: c, Actual: Describe(Frontmatter.Read(c.Raw))))
            .Where(pair => !JsonNode.DeepEquals(pair.Case.Expected, pair.Actual))
            .Select(pair => $"{Show(pair.Case.Raw)} -> {pair.Actual.ToJsonString()}, recorded {pair.Case.Expected.ToJsonString()}")
            .ToList();

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {cases.Count} note(s) differ from the recording:\n{string.Join('\n', mismatches.Take(40))}");
    }

    [Fact]
    public void A_refused_note_says_why()
    {
        var read = Frontmatter.Parse("---\ntype: [unclosed\n---\nBody\n");

        Assert.NotNull(read.Error);
        Assert.Empty(read.Metadata);
    }

    [Fact]
    public void A_string_field_reads_as_text_and_any_other_value_as_absent()
    {
        var read = Frontmatter.Parse("---\ntype: resource\ncreated: 2024-01-31\n---\n");

        Assert.Equal("resource", read.Text("type"));
        Assert.Null(read.Text("created"));
        Assert.Equal(new YamlScalar("2024-01-31"), read.Get("created"));
        Assert.Null(read.Get("missing"));
    }

    // PyYAML raises KeyError, IndexError or AttributeError on these, which the recorded lint never caught; C# refuses the note.
    [Theory]
    [InlineData("type: !!bool maybe")]
    [InlineData("type: !!int ''")]
    [InlineData("type: !!float ''")]
    [InlineData("type: !!timestamp nope")]
    public void A_value_that_crashed_the_recording_is_refused(string line) =>
        Assert.NotNull(Frontmatter.Parse($"---\n{line}\n---\nBody\n").Error);

    [Fact]
    public void A_stream_of_two_documents_is_refused() => Assert.Throws<VaultYamlException>(() => VaultYaml.Load("a: 1\n---\nb: 2\n"));

    // Known gap: PyYAML reads a block scalar whose first line is only spaces as '', YamlDotNet refuses it, so such a note
    // is logged and passed over where the recording read it. Pinned so a fix shows up here.
    [Fact]
    public void A_block_scalar_opened_by_a_line_of_spaces_is_refused_where_pyyaml_read_it_as_empty() =>
        Assert.Throws<VaultYamlException>(() => VaultYaml.Load("status: |\n  \ncreated: x\n"));

    [Fact]
    public void An_empty_stream_holds_no_document() => Assert.Null(VaultYaml.Load(string.Empty));

    [Fact]
    public void A_missing_file_is_refused_not_thrown() =>
        Assert.NotNull(Frontmatter.Load(Path.Combine(Path.GetTempPath(), "rig-no-such-note", "note.md")).Error);

    [Theory]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1.5e-5, "1.5e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(123.45, "123.45")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(double.NegativeInfinity, "-inf")]
    public void Floats_print_as_recorded(double value, string expected) => Assert.Equal(expected, YamlFloat.Repr(value));

    [Theory]
    [InlineData("12", 10, 12)]
    [InlineData(" -0x1f ", 16, -31)]
    [InlineData("0o17", 8, 15)]
    [InlineData("+101", 2, 5)]
    public void Ints_parse_as_recorded(string text, int radix, long expected) =>
        Assert.Equal(expected, (long)YamlInt.Parse(text, radix));

    [Theory]
    [InlineData("", 10)]
    [InlineData("12a", 10)]
    [InlineData("0x", 16)]
    [InlineData("8", 8)]
    [InlineData("1-2", 10)]
    public void A_bad_int_is_refused(string text, int radix) => Assert.Throws<VaultYamlException>(() => YamlInt.Parse(text, radix));

    [Theory]
    [InlineData("inf", double.PositiveInfinity)]
    [InlineData("-Infinity", double.NegativeInfinity)]
    [InlineData(" 1.5e3 ", 1500.0)]
    public void Floats_parse_as_recorded(string text, double expected) => Assert.Equal(expected, YamlFloat.Parse(text));

    [Fact]
    public void Nan_parses() => Assert.True(double.IsNaN(YamlFloat.Parse("NaN")));

    [Fact]
    public void A_bad_float_is_refused() => Assert.Throws<VaultYamlException>(() => YamlFloat.Parse("1e"));

    private static JsonObject Describe(FrontmatterRead read)
    {
        if (read.Error is not null)
        {
            return new JsonObject { ["error"] = true };
        }

        var fields = new JsonObject();
        foreach (var name in Fields.Where(read.Metadata.ContainsKey))
        {
            fields[name] = Kind(read.Metadata[name]);
        }

        return new JsonObject { ["fields"] = fields };
    }

    private static JsonObject Kind(YamlValue value) => value switch
    {
        YamlNull => new JsonObject { ["k"] = "null" },
        YamlString text => new JsonObject { ["k"] = "str", ["v"] = text.Text },
        YamlList list => new JsonObject { ["k"] = "list", ["items"] = new JsonArray([.. list.Items.Select(Rendered)]) },
        _ => new JsonObject { ["k"] = "other" },
    };

    private static JsonNode? Rendered(YamlValue item) => item switch
    {
        YamlString text => text.Text,
        YamlNull => "None",
        YamlScalar scalar => scalar.Rendered,
        _ => null,
    };

    private static string Show(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        return JsonValue.Create(text.Length > 120 ? text[..120] : text).ToJsonString();
    }

    private static IEnumerable<OracleCase> Load(string path)
    {
        foreach (var node in JsonNode.Parse(File.ReadAllText(path))!.AsArray())
        {
            var item = node!.AsObject();
            yield return new OracleCase(Convert.FromBase64String(item["b64"]!.GetValue<string>()), item["expect"]!.AsObject());
        }
    }

    private sealed record OracleCase(byte[] Raw, JsonObject Expected);
}
