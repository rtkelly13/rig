using System.Text;
using System.Text.Json.Nodes;
using Rig.Core.Vault;

namespace Rig.Core.Tests.Vault;

/// <summary>
/// Recorded notes with what the frontmatter reader makes of them. <c>frontmatter-oracle.json</c> records,
/// for each note's raw bytes, whether it was refused and otherwise the kind of every key the lint reads: the
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
        var cases = Load(Path.Combine(AppContext.BaseDirectory, "Vault", "frontmatter-oracle.json")).ToList();
        var extra = Environment.GetEnvironmentVariable(ExtraFixtureVariable);
        if (!string.IsNullOrEmpty(extra))
        {
            cases.AddRange(Load(extra));
        }

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

    [Theory]
    [InlineData("type: !!bool maybe")]
    [InlineData("type: !!int 5")]
    public void A_tag_other_than_str_is_refused(string line) =>
        Assert.NotNull(Frontmatter.Parse($"---\n{line}\n---\nBody\n").Error);

    [Fact]
    public void A_stream_of_two_documents_is_refused() => Assert.Throws<VaultYamlException>(() => VaultYaml.Load("a: 1\n---\nb: 2\n"));

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
    public void Floats_print_with_a_point_or_an_exponent(double value, string expected) => Assert.Equal(expected, YamlFloat.Repr(value));

    [Theory]
    [InlineData("12", "12")]
    [InlineData("-0x1f", "-31")]
    [InlineData("1_000", "1000")]
    [InlineData("+7", "7")]
    [InlineData("1.50", "1.5")]
    [InlineData("-.inf", "-inf")]
    [InlineData(".NaN", "nan")]
    [InlineData("yes", "True")]
    [InlineData("Off", "False")]
    [InlineData("2024-01-31", "2024-01-31")]
    [InlineData("2024-01-31 10:20:30", "2024-01-31 10:20:30")]
    public void A_plain_scalar_of_another_type_renders_as_its_value(string text, string rendered) =>
        Assert.Equal(new YamlScalar(rendered), VaultYaml.Load(text));

    [Theory]
    [InlineData("0755")]
    [InlineData("1e3")]
    [InlineData("2024-13-45")]
    [InlineData("1.2.3")]
    [InlineData("'12'")]
    public void Anything_else_stays_a_string(string text) => Assert.IsType<YamlString>(VaultYaml.Load(text));

    [Fact]
    public void A_merge_key_adds_the_entries_it_names_behind_the_ones_written()
    {
        var loaded = Assert.IsType<YamlMap>(VaultYaml.Load("base: &b {a: 1, b: 2}\nuse:\n  <<: *b\n  b: 3\n"));
        var use = Assert.IsType<YamlMap>(loaded.Entries[1].Value);

        Assert.Equal([new(new YamlString("b"), new YamlScalar("3")), new(new YamlString("a"), new YamlScalar("1"))], use.Entries);
    }

    [Fact]
    public void A_repeated_key_keeps_its_place_and_its_last_value()
    {
        var loaded = Assert.IsType<YamlMap>(VaultYaml.Load("a: 1\nb: 2\na: 3\n"));

        Assert.Equal([new(new YamlString("a"), new YamlScalar("3")), new(new YamlString("b"), new YamlScalar("2"))], loaded.Entries);
    }

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
