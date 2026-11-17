namespace Rig.Image.Tests;

public sealed class PromotionOracleTests
{
    public static TheoryData<string> Candidates()
    {
        var data = new TheoryData<string>();
        foreach (var item in OracleFixture.Document.Property("promotion").Items())
        {
            data.Add(item.Property("candidate_render_id").AsString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Candidates))]
    public void VerdictMatchesTheOracle(string candidateRenderId)
    {
        var entry = OracleFixture.Document.Property("promotion").Items().Single(item => item.Property("candidate_render_id").AsString() == candidateRenderId);
        var boxes = FeatureRegions.For(entry.Property("project").AsString());
        var reference = RasterPipeline.LoadNormalised(OracleFixture.RepoBytes(entry.Property("reference").AsString()));
        var incumbent = RasterPipeline.Normalise(OracleFixture.RenderPixels(entry.Property("incumbent_render_id").AsString()));
        var candidate = RasterPipeline.Normalise(OracleFixture.RenderPixels(candidateRenderId));

        var verdict = PromotionGate.Evaluate(
            DriftMetrics.ScoreCandidate(reference, incumbent, boxes),
            DriftMetrics.Ssim(reference, incumbent),
            DriftMetrics.ScoreCandidate(reference, candidate, boxes),
            DriftMetrics.Ssim(reference, candidate));

        Assert.Equal(entry.Property("promotable").AsBoolean(), verdict.Promotable);
        Assert.Equal(entry.Property("composite_delta").AsNumber(), verdict.CompositeDelta, 1e-6);
        Assert.Equal(entry.Property("ssim_delta").AsNumber(), verdict.SsimDelta, 1e-6);
        Assert.Equal(entry.Property("worst_feature_delta").AsNumber(), verdict.WorstFeatureDelta, 1e-6);
        Assert.Equal(entry.Property("worst_feature_key").AsStringOrNull(), verdict.WorstFeatureKey);
        Assert.Equal(entry.Property("reasons").Items().Select(reason => reason.AsString()), verdict.Reasons);
    }

    [Fact]
    public void OracleHoldsBothPromotableAndRejectedCandidates()
    {
        var verdicts = OracleFixture.Document.Property("promotion").Items().Select(item => item.Property("promotable").AsBoolean()).ToList();

        Assert.Contains(true, verdicts);
        Assert.Contains(false, verdicts);
    }
}
