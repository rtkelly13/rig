using System.Text.Json;
using System.Text.Json.Serialization;
using Rig.Core.Text;

namespace Rig.Core.Tests.Text;

/// <summary>
/// The typed boundary: System.Text.Json reads a record under a strict context and every rejection reads as
/// <c>$.path: expected X, found Y</c>, the same as a hand-written reader's.
/// </summary>
public sealed partial class JsonProblemTypedTests
{
    [Theory]
    [InlineData("""{"name": "a", "extra": 1}""", "$.extra: expected a known field, found the number 1")]
    [InlineData("""{"name": "a", "tags": [{"label": "x", "nope": true}]}""", "$.tags[0].nope: expected a known field, found true")]
    [InlineData("""{"name": "a", "name": "b"}""", "$.name: expected a field that appears once, found it repeated")]
    [InlineData("""{}""", "$.name: expected a string, found nothing")]
    [InlineData("""{"name": "a", "map": {"k": 1, "k": 2}}""", "$.map.k: expected a field that appears once, found it repeated")]
    [InlineData("""{"name": null}""", "$.name: expected a string, found null")]
    [InlineData("""{"name": 7}""", "$.name: expected a string, found the number 7")]
    [InlineData("""{"name": "a", "plain": "12"}""", "$.plain: expected a whole number, found the string \"12\"")]
    [InlineData("""{"name": "a", "plain": 12.0}""", "$.plain: expected a whole number, found the number 12.0")]
    [InlineData("""{"name": "a", "plain": true}""", "$.plain: expected a whole number, found true")]
    [InlineData("""{"name": "a", "big": "1x"}""", "$.big: expected a whole number or a string of digits, found the string \"1x\"")]
    [InlineData("""{"name": "a", "big": 12.0}""", "$.big: expected a whole number or a string of digits, found the number 12.0")]
    [InlineData("""{"name": "a", "big": true}""", "$.big: expected a whole number or a string of digits, found true")]
    [InlineData("""{"name": "a", "flag": 1}""", "$.flag: expected true or false, found the number 1")]
    [InlineData("""{"name": "a", "ratio": "x"}""", "$.ratio: expected a number, found the string \"x\"")]
    [InlineData("""{"name": "a", "id": ""}""", "$.id: expected a non-empty string, found the string \"\"")]
    [InlineData("""{"name": "a", "id": null}""", "$.id: expected a non-empty string, found null")]
    [InlineData("""{"name": "a", "tags": [null]}""", "$.tags[0]: expected an object, found null")]
    [InlineData("""{"name": "a", "tags": [{"label": 5}]}""", "$.tags[0].label: expected a string, found the number 5")]
    [InlineData("""{"name": "a", "tags": [{"label": "x"}, 3]}""", "$.tags[1]: expected an object, found the number 3")]
    [InlineData("""{"name": "a", "tags": {}}""", "$.tags: expected a list, found an object")]
    [InlineData("""{"name": "a", "tags": null}""", "$.tags: expected a list, found null")]
    [InlineData("""{"name": "a", "map": {"k": "x"}}""", "$.map.k: expected a whole number, found the string \"x\"")]
    [InlineData("""{"name": "a", "map": {"odd key": "x"}}""", "$.map['odd key']: expected a whole number, found the string \"x\"")]
    [InlineData("""[1]""", "$: expected an object, found a list")]
    [InlineData("""5""", "$: expected an object, found the number 5")]
    [InlineData("""null""", "$: expected an object, found null")]
    public void A_strict_read_names_the_path_what_was_expected_and_what_was_found(string json, string problem)
    {
        var thrown = Assert.Throws<JsonInputException>(() => JsonProblem.Read(json, Fixtures.Default.Strict));

        Assert.Equal(problem, thrown.Message);
    }

    [Theory]
    [InlineData("""{"kind": "cat", "name": "x"}""", null)]
    [InlineData("""{"name": "x"}""", "$.kind: expected a known type, found nothing")]
    [InlineData("""{"kind": "bird"}""", "$.kind: expected a known type, found the string \"bird\"")]
    [InlineData("""{"kind": "dog", "name": "x"}""", "$.name: expected a known field, found the string \"x\"")]
    [InlineData("""{"kind": "dog"}""", "$.age: expected a whole number, found nothing")]
    public void A_polymorphic_read_names_a_missing_or_unknown_discriminator_without_the_runtime_s_words(string json, string? problem)
    {
        var thrown = Record.Exception(() => JsonProblem.Read(json, Fixtures.Default.Pet));

        Assert.Equal(problem, (thrown as JsonInputException)?.Message);
    }

    [Fact]
    public void A_foreign_read_ignores_what_it_does_not_read_and_keeps_the_last_of_a_repeated_member()
    {
        var read = JsonProblem.Read("""{"name": "a", "name": "b", "unread": {"x": [1]}}""", ForeignFixtures.Default.Foreign);

        Assert.Equal("b", read.Name);
    }

    [Fact]
    public void A_foreign_read_still_types_what_it_reads()
    {
        var thrown = Assert.Throws<JsonInputException>(() => JsonProblem.Read("""{"name": 3, "unread": 1}""", ForeignFixtures.Default.Foreign));

        Assert.Equal("$.name: expected a string, found the number 3", thrown.Message);
    }

    [Fact]
    public void An_int64_reads_from_a_string_only_where_the_property_says_so()
    {
        var read = JsonProblem.Read("""{"name": "a", "big": "12", "plain": 12, "map": {"k": 3}}""", Fixtures.Default.Strict);

        Assert.Equal((12L, 12L, 3L), (read.Big, read.Plain, read.Map!["k"]));
    }

    [Fact]
    public void Text_that_is_not_json_is_reported_as_such()
    {
        var thrown = Assert.Throws<JsonInputException>(() => JsonProblem.Read("""{"torn": """, Fixtures.Default.Strict));

        Assert.StartsWith("$: not valid JSON: ", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejection_in_an_element_is_relative_to_that_element()
    {
        using var document = JsonDocument.Parse("""{"name": 5}""");

        var thrown = Assert.Throws<JsonInputException>(() => JsonProblem.Read(document.RootElement, Fixtures.Default.Strict));

        Assert.Equal("f.jsonl line 2: $.spans[1].name: expected a string, found the number 5", thrown.Under("f.jsonl line 2", "$.spans[1]").Message);
    }

    [Fact]
    public void An_element_of_null_is_rejected_as_null()
    {
        using var document = JsonDocument.Parse("null");

        Assert.Equal("$: expected an object, found null", Assert.Throws<JsonInputException>(() => JsonProblem.Read(document.RootElement, Fixtures.Default.Strict)).Message);
    }

    [Fact]
    public void A_rejection_the_metadata_cannot_place_keeps_the_parsers_words()
    {
        var thrown = Assert.Throws<JsonInputException>(() => JsonProblem.Read("""{"name": "a", "ghost": 1}""", Fixtures.Default.Odd));

        Assert.Equal("$.ghost.deeper: broken converter", thrown.Message);
    }

    [Fact]
    public void Run_names_the_source_of_a_typed_rejection()
    {
        var thrown = Assert.Throws<JsonInputException>(() => JsonProblem.Run("f.json", () => JsonProblem.Read("""{"name": 1}""", Fixtures.Default.Strict)));

        Assert.Equal("f.json: $.name: expected a string, found the number 1", thrown.Message);
    }

    internal sealed record Strict
    {
        public required string Name { get; init; }

        [JsonConverter(typeof(NonEmptyStringConverter))]
        public string Id { get; set; } = "x";

        public long? Plain { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? Big { get; set; }

        public bool? Flag { get; set; }

        public double? Ratio { get; set; }

        public List<Tag> Tags { get; set; } = [];

        public Dictionary<string, long>? Map { get; set; }
    }

    internal record struct Tag
    {
        public Tag()
        {
        }

        public string? Label { get; set; }
    }

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
    [JsonDerivedType(typeof(Cat), "cat")]
    [JsonDerivedType(typeof(Dog), "dog")]
    internal abstract record Pet;

    internal sealed record Cat(string Name) : Pet;

    internal sealed record Dog(int Age) : Pet;

    internal sealed record Foreign
    {
        public required string Name { get; init; }
    }

    internal sealed record Odd
    {
        public required string Name { get; init; }

        [JsonConverter(typeof(Breaking))]
        public int? Ghost { get; set; }
    }

    internal sealed class Breaking : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new JsonException("broken converter", "$.ghost.deeper", null, null);

        public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options) => writer.WriteNullValue();
    }

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Strict,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(Strict))]
    [JsonSerializable(typeof(Odd))]
    [JsonSerializable(typeof(Pet))]
    internal sealed partial class Fixtures : JsonSerializerContext;

    [JsonSourceGenerationOptions(
        JsonSerializerDefaults.Strict,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        AllowDuplicateProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true)]
    [JsonSerializable(typeof(Foreign))]
    internal sealed partial class ForeignFixtures : JsonSerializerContext;
}
