using System.Security.Cryptography;

namespace Rig.Image.Tests;

public sealed class RasterOracleTests
{
    public static TheoryData<string> References()
    {
        var data = new TheoryData<string>();
        foreach (var item in OracleFixture.Document.Property("normalised_references").Items())
        {
            data.Add(item.Property("reference").AsString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(References))]
    public void NormalisedReferenceMatchesPillowBitForBit(string reference)
    {
        var entry = OracleFixture.Document.Property("normalised_references").Items().Single(item => item.Property("reference").AsString() == reference);

        var normalised = RasterPipeline.LoadNormalised(OracleFixture.RepoBytes(reference));

        Assert.Equal(entry.Property("rgb_sha256").AsString(), Convert.ToHexStringLower(SHA256.HashData(normalised.Pixels)));
    }

    [Theory]
    [InlineData("rtkelly-labs.master.500")]
    [InlineData("parquet-source-generator.master.500")]
    public void DecodedOracleRenderMatchesItsHash(string id)
    {
        var render = OracleFixture.RenderPixels(id);

        Assert.Equal(OracleFixture.Render(id).Property("rgba_sha256").AsString(), Convert.ToHexStringLower(SHA256.HashData(render.Pixels)));
    }
}
