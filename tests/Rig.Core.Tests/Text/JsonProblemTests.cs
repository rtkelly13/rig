using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Tests.Text;

public sealed class JsonProblemTests
{
    [Fact]
    public void A_value_of_the_wrong_type_reads_as_source_path_expected_found()
    {
        var root = JsonNode.Parse("""{"spans": [{"id": 5}]}""")!;
        var span = root["spans"]![0]!.AsObject();

        var problem = JsonProblem.Expected(span, "id", "a string", span["id"]).Within("trace.jsonl line 3");

        Assert.Equal("trace.jsonl line 3: $.spans[0].id: expected a string, found the number 5", problem.Message);
        Assert.Equal(("trace.jsonl line 3", "$.spans[0].id"), (problem.Input, problem.Path));
    }

    [Fact]
    public void A_missing_field_is_found_as_nothing()
    {
        var parent = JsonNode.Parse("""{}""")!.AsObject();

        Assert.Equal("$.name: expected a string, found nothing", JsonProblem.Missing(parent, "name", "a string").Message);
    }

    [Fact]
    public void Under_places_the_path_below_a_prefix()
    {
        var problem = JsonProblem.Expected("$.attributes", "a list", "an object").Under("f.jsonl", "$.spans[2]");

        Assert.Equal("f.jsonl: $.spans[2].attributes: expected a list, found an object", problem.Message);
    }

    [Fact]
    public void Run_names_the_source_once_and_keeps_an_already_named_one()
    {
        var named = Assert.Throws<JsonInputException>(() => JsonProblem.Run<int>("outer", () => throw JsonProblem.Expected("$.a", "x", "y").Within("inner")));
        var unnamed = Assert.Throws<JsonInputException>(() => JsonProblem.Run<int>("outer", () => throw JsonProblem.Expected("$.a", "x", "y")));

        Assert.Equal(("inner: $.a: expected x, found y", "outer: $.a: expected x, found y"), (named.Message, unnamed.Message));
    }

    [Fact]
    public void Bad_json_is_wrapped_with_the_parser_error_as_its_cause()
    {
        var problem = Assert.Throws<JsonInputException>(() => JsonProblem.Parse("state.json", "{\"a\": "));

        Assert.StartsWith("state.json: $: not valid JSON: ", problem.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<JsonException>(problem.InnerException);
        Assert.DoesNotContain("LineNumber", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "null")]
    [InlineData("true", "true")]
    [InlineData("5", "the number 5")]
    [InlineData("5.0", "the number 5.0")]
    [InlineData("\"hi\"", "the string \"hi\"")]
    [InlineData("[1]", "a list")]
    [InlineData("{}", "an object")]
    public void Describe_words_every_json_kind(string json, string words)
    {
        Assert.Equal(words, JsonProblem.Describe(JsonNode.Parse(json)));
        Assert.Equal(words, JsonProblem.Describe(JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void A_long_string_is_quoted_truncated()
    {
        var words = JsonProblem.Describe(JsonNode.Parse($"\"{new string('x', 100)}\""));

        Assert.Equal($"the string \"{new string('x', 40)}...\"", words);
    }

    [Theory]
    [InlineData("name", "$.a.name")]
    [InlineData("1", "$.a.1")]
    [InlineData("has space", "$.a['has space']")]
    [InlineData("", "$.a['']")]
    public void Child_quotes_keys_that_are_not_plain(string key, string path) =>
        Assert.Equal(path, JsonProblem.Child("$.a", key));

    [Theory]
    [InlineData("""{"n": 5}""", 5L)]
    [InlineData("""{"n": -5}""", -5L)]
    public void A_whole_number_reads_an_integer(string json, long expected) =>
        Assert.Equal(expected, JsonProblem.WholeNumber(JsonDocument.Parse(json).RootElement, "n", "$"));

    [Theory]
    [InlineData("""{"n": 5.5}""", "$.n: expected a whole number, found the number 5.5")]
    [InlineData("""{"n": "5"}""", "$.n: expected a whole number, found the string \"5\"")]
    [InlineData("""{"n": true}""", "$.n: expected a whole number, found true")]
    [InlineData("""{}""", "$.n: expected a whole number, found nothing")]
    public void A_whole_number_rejects_everything_else(string json, string message) =>
        Assert.Equal(message, Assert.Throws<JsonInputException>(() => JsonProblem.WholeNumber(JsonDocument.Parse(json).RootElement, "n", "$")).Message);
}
