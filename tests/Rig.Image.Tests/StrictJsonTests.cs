namespace Rig.Image.Tests;

public sealed class StrictJsonTests
{
    [Fact]
    public void MissingPropertyNamesFileAndPath()
    {
        var root = StrictJson.Parse("oracle.json", """{"a": {"b": 1}}""");

        var exception = Assert.Throws<FixtureFormatException>(() => root.Property("a").Property("c"));

        Assert.Equal("oracle.json: $.a: expected property 'c', found an object with properties [b]", exception.Message);
    }

    [Fact]
    public void StringWhereNumberExpectedIsNotCoerced()
    {
        var root = StrictJson.Parse("oracle.json", """{"items": [{"size": "48"}]}""");

        var exception = Assert.Throws<FixtureFormatException>(() => root.Property("items").Items()[0].Property("size").AsInteger());

        Assert.StartsWith("oracle.json: $.items[0].size: expected", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FractionWhereIntegerExpectedIsRejected()
    {
        var root = StrictJson.Parse("oracle.json", """{"size": 4.5}""");

        var exception = Assert.Throws<FixtureFormatException>(() => root.Property("size").AsInteger());

        Assert.StartsWith("oracle.json: $.size: expected an integer", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumberWhereStringExpectedIsRejected()
    {
        var root = StrictJson.Parse("oracle.json", """{"name": 7}""");

        Assert.Throws<FixtureFormatException>(() => root.Property("name").AsString());
    }

    [Fact]
    public void BooleanIsNotAcceptedAsANumberOrNull()
    {
        var root = StrictJson.Parse("oracle.json", """{"flag": true, "none": null}""");

        Assert.True(root.Property("flag").AsBoolean());
        Assert.Null(root.Property("none").AsStringOrNull());
        Assert.Throws<FixtureFormatException>(() => root.Property("flag").AsNumber());
    }

    [Fact]
    public void UnparseableTextNamesTheFile()
    {
        var exception = Assert.Throws<FixtureFormatException>(() => StrictJson.Parse("oracle.json", "{not json"));

        Assert.StartsWith("oracle.json: $: expected JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayWhereObjectExpectedIsRejected()
    {
        var root = StrictJson.Parse("oracle.json", "[1]");

        Assert.Throws<FixtureFormatException>(() => root.PropertyNames());
        Assert.Equal(1, root.Items().Single().AsInteger());
    }
}
