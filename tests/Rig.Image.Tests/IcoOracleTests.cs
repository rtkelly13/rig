using System.Security.Cryptography;

namespace Rig.Image.Tests;

public sealed class IcoOracleTests
{
    public static TheoryData<string> Icons()
    {
        var data = new TheoryData<string>();
        foreach (var item in OracleFixture.Document.Property("icons").Items())
        {
            data.Add(item.Property("file").AsString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Icons))]
    public void EntriesMatchPillowsFrozenIcon(string file)
    {
        var icon = OracleFixture.Document.Property("icons").Items().Single(item => item.Property("file").AsString() == file);
        var source = RasterPipeline.Decode(OracleFixture.Bytes($"renders/{icon.Property("source").AsString()}"));

        var entries = IcoFile.Read(IcoFile.FromFavicon(source));

        var expected = icon.Property("entries").Items();
        Assert.Equal(expected.Select(entry => entry.Property("size").AsInteger()).Order(), entries.Select(entry => entry.Size).Order());
        foreach (var entry in entries)
        {
            var pixels = RasterPipeline.Decode(entry.Png.Span);
            var hash = expected.Single(item => item.Property("size").AsInteger() == entry.Size).Property("rgba_sha256").AsString();
            Assert.Equal(hash, Convert.ToHexStringLower(SHA256.HashData(pixels.Pixels)));
        }
    }

    [Theory]
    [MemberData(nameof(Icons))]
    public void FrozenIconReadsBackWithTheOraclesSizes(string file)
    {
        var icon = OracleFixture.Document.Property("icons").Items().Single(item => item.Property("file").AsString() == file);

        var entries = IcoFile.Read(OracleFixture.Bytes(file));

        Assert.Equal(icon.Property("entries").Items().Select(entry => entry.Property("size").AsInteger()).Order(), entries.Select(entry => entry.Size).Order());
    }

    [Fact]
    public void WrittenContainerReadsBack()
    {
        var png = RasterPipeline.EncodePng(new Raster(2, 2, new byte[16]));

        var entries = IcoFile.Read(IcoFile.Write([new IcoEntry(2, png)]));

        Assert.Equal(2, entries.Single().Size);
        Assert.Equal(png, entries.Single().Png.ToArray());
    }

    [Fact]
    public void NonIconBytesAreRejectedWithWhatWasExpected()
    {
        var exception = Assert.Throws<InvalidDataException>(() => IcoFile.Read(new byte[] { 1, 2, 3 }));

        Assert.Contains("expected an ICO file", exception.Message, StringComparison.Ordinal);
    }
}
