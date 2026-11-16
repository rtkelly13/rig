using System.Text.Json.Nodes;
using Rig.Core.Text;

namespace Rig.Core.Tests.Text;

/// <summary>
/// The one reader for a JSON object's fields: the strict accessors keep pydantic's "expected …" failure, and the
/// <c>As*</c>/<c>Field*</c> pair answers null for payloads another tool owns.
/// </summary>
public sealed class JsonShapeTests
{
    private static JsonObject Payload() => JsonNode.Parse(
        """{"name": "rig", "count": 3, "flag": true, "none": null, "tags": ["a", 2], "child": {"k": 1}}""")!.AsObject();

    [Fact]
    public void Strict_accessors_read_the_fields_they_name()
    {
        var node = Payload();

        Assert.Equal("rig", JsonShape.String(node, "name"));
        Assert.Equal(3, JsonShape.Int(node, "count"));
        Assert.True(JsonShape.Bool(node, "flag"));
        Assert.Equal("rig", JsonShape.OptionalString(node, "name"));
        Assert.Equal(3, JsonShape.OptionalInt(node, "count"));
        Assert.True(JsonShape.OptionalBool(node, "flag"));
        Assert.Equal(1, JsonShape.Int(JsonShape.Object(JsonShape.Field(node, "child")), "k"));
        Assert.Equal(["a", "2"], JsonShape.Scalars(node, "tags"));
    }

    [Fact]
    public void Strict_accessors_default_an_absent_field()
    {
        var node = Payload();

        Assert.Equal(string.Empty, JsonShape.String(node, "absent"));
        Assert.False(JsonShape.Bool(node, "absent"));
        Assert.Empty(JsonShape.Scalars(node, "absent"));
        Assert.Empty(JsonShape.Items(node, "absent"));
        Assert.Null(JsonShape.OptionalString(node, "absent"));
        Assert.Null(JsonShape.OptionalInt(node, "absent"));
        Assert.Null(JsonShape.OptionalBool(node, "absent"));
        Assert.Null(JsonShape.DefaultedObject(node, "absent"));
    }

    [Fact]
    public void Strict_accessors_treat_a_present_null_as_optional_or_misshapen()
    {
        var node = Payload();

        Assert.Null(JsonShape.OptionalString(node, "none"));
        Assert.Null(JsonShape.OptionalInt(node, "none"));
        Assert.Null(JsonShape.OptionalBool(node, "none"));
        Assert.Null(JsonShape.OptionalObject(node, "none"));
        _ = Assert.Throws<FormatException>(() => JsonShape.String(node, "none"));
        _ = Assert.Throws<FormatException>(() => JsonShape.Int(node, "none"));
        _ = Assert.Throws<FormatException>(() => JsonShape.Bool(node, "none"));
        _ = Assert.Throws<FormatException>(() => JsonShape.DefaultedObject(node, "none"));
    }

    [Theory]
    [InlineData("count", "a string")]
    [InlineData("flag", "a string")]
    public void A_strict_string_read_refuses_another_kind(string key, string expected)
    {
        var failure = Assert.Throws<FormatException>(() => JsonShape.String(Payload(), key));

        Assert.Equal($"expected {key} as {expected}", failure.Message);
    }

    [Fact]
    public void A_strict_integer_read_refuses_another_kind()
    {
        var failure = Assert.Throws<FormatException>(() => JsonShape.Int(Payload(), "name"));

        Assert.Equal("expected name as an integer", failure.Message);
    }

    [Fact]
    public void A_strict_object_or_array_read_refuses_another_kind()
    {
        var node = Payload();

        _ = Assert.Throws<FormatException>(() => JsonShape.OptionalObject(node, "name"));
        _ = Assert.Throws<FormatException>(() => JsonShape.Items(node, "name"));
    }

    [Fact]
    public void TryRead_turns_one_misshapen_field_into_null()
    {
        var node = Payload();

        Assert.Null(JsonShape.TryRead(() => JsonShape.Object(JsonShape.Field(node, "name"))));
        Assert.NotNull(JsonShape.TryRead(() => JsonShape.Object(JsonShape.Field(node, "child"))));
    }

    [Fact]
    public void The_lenient_accessors_answer_null_instead_of_throwing()
    {
        var node = Payload();

        Assert.Null(JsonShape.AsText(JsonShape.Field(node, "count")));
        Assert.Null(JsonShape.AsInteger(JsonShape.Field(node, "name")));
        Assert.Null(JsonShape.AsInt32(JsonShape.Field(node, "name")));
        Assert.Null(JsonShape.AsBool(JsonShape.Field(node, "name")));
        Assert.Null(JsonShape.AsObject(JsonShape.Field(node, "tags")));
        Assert.Null(JsonShape.AsArray(JsonShape.Field(node, "name")));
    }

    [Fact]
    public void The_lenient_field_reads_answer_null_for_an_absent_field_or_a_foreign_parent()
    {
        var node = Payload();

        Assert.Null(JsonShape.FieldText(node, "absent"));
        Assert.Null(JsonShape.FieldInteger(node, "absent"));
        Assert.Null(JsonShape.FieldBool(node, "absent"));
        Assert.Null(JsonShape.FieldObject(node, "absent"));

        // A node that is not an object at all is simply empty, as the registry parsers need.
        Assert.Null(JsonShape.FieldText(JsonShape.FieldObject(node, "tags"), "name"));
        Assert.Null(JsonShape.FieldText(null, "name"));
    }

    [Fact]
    public void The_lenient_accessors_read_the_fields_they_name()
    {
        var node = Payload();

        Assert.Equal("rig", JsonShape.FieldText(node, "name"));
        Assert.Equal(3, JsonShape.FieldInteger(node, "count"));
        Assert.True(JsonShape.FieldBool(node, "flag"));
        Assert.Equal(1, JsonShape.FieldInteger(JsonShape.FieldObject(node, "child"), "k"));
        Assert.Equal(["a", "2"], JsonShape.Scalars(node, "tags"));
    }
}
