namespace Rig.Image.Tests;

public sealed class MetricsOracleTests
{
    /// <summary>
    /// Measured against the oracle: the largest difference is 1e-7 of a percentage point (the Python reduces float32 arrays, the
    /// port reduces doubles), so agreement is gated at 1e-6, ten times the measured worst case.
    /// </summary>
    private const double Tolerance = 1e-6;

    public static TheoryData<string> Metrics()
    {
        var data = new TheoryData<string>();
        foreach (var item in OracleFixture.Document.Property("metrics").Items())
        {
            data.Add(item.Property("id").AsString());
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Metrics))]
    public void DriftMatchesTheOracle(string id)
    {
        var entry = Entry(id);
        var drift = DriftMetrics.Drift(Reference(entry), Rendered(entry));
        var expected = entry.Property("drift");

        Assert.Equal(expected.Property("rmse_pct").AsNumber(), drift.RmsePct, Tolerance);
        Assert.Equal(expected.Property("ssim").AsNumber(), drift.Ssim, Tolerance);
        Assert.Equal(expected.Property("psnr_db").AsNumber(), drift.PsnrDb, Tolerance);
        Assert.Equal(expected.Property("luminance_rmse_pct").AsNumber(), drift.LuminanceRmsePct, Tolerance);
        Assert.Equal(expected.Property("chrominance_rmse_pct").AsNumber(), drift.ChrominanceRmsePct, Tolerance);
        Assert.Equal(expected.Property("color_to_geometry_ratio").AsNumber(), drift.ColorToGeometryRatio, Tolerance);
        Assert.Equal(expected.Property("edge_correlation").AsNumber(), drift.EdgeCorrelation, Tolerance);
        Assert.Equal(expected.Property("boundary_dilation_bias").AsNumber(), drift.BoundaryDilationBias, Tolerance);
    }

    [Theory]
    [MemberData(nameof(Metrics))]
    public void GridAndHotspotsMatchTheOracle(string id)
    {
        var entry = Entry(id);
        var drift = DriftMetrics.Drift(Reference(entry), Rendered(entry));
        var expected = entry.Property("drift");

        var grid = expected.Property("grid").Items();
        Assert.Equal(grid.Count, drift.Grid.Count);
        for (var i = 0; i < grid.Count; i++)
        {
            Assert.Equal(grid[i].Property("row").AsInteger(), drift.Grid[i].Row);
            Assert.Equal(grid[i].Property("column").AsInteger(), drift.Grid[i].Column);
            Assert.Equal(grid[i].Property("error").AsNumber(), drift.Grid[i].Error, Tolerance);
        }

        var hotspots = expected.Property("hotspots").Items();
        Assert.Equal(hotspots.Count, drift.Hotspots.Count);
        for (var i = 0; i < hotspots.Count; i++)
        {
            Assert.Equal(hotspots[i].Property("rank").AsInteger(), drift.Hotspots[i].Rank);
            Assert.Equal(hotspots[i].Property("x0").AsInteger(), drift.Hotspots[i].X0);
            Assert.Equal(hotspots[i].Property("y0").AsInteger(), drift.Hotspots[i].Y0);
            Assert.Equal(hotspots[i].Property("x1").AsInteger(), drift.Hotspots[i].X1);
            Assert.Equal(hotspots[i].Property("y1").AsInteger(), drift.Hotspots[i].Y1);
            Assert.Equal(hotspots[i].Property("error_pct").AsNumber(), drift.Hotspots[i].ErrorPct, 1e-12);
            Assert.Equal(hotspots[i].Property("dominant_mode").AsString(), drift.Hotspots[i].Mode);
        }
    }

    [Theory]
    [MemberData(nameof(Metrics))]
    public void ScoreMatchesTheOracle(string id)
    {
        var entry = Entry(id);
        var project = entry.Property("project").AsString();

        var score = DriftMetrics.ScoreCandidate(Reference(entry), Rendered(entry), FeatureRegions.For(project));

        var expected = entry.Property("score");
        Assert.Equal(expected.Property("composite_pct").AsNumber(), score.CompositePct, Tolerance);
        Assert.Equal(expected.Property("full_frame_pct").AsNumber(), score.FullFramePct, Tolerance);
        var features = expected.Property("per_feature_pct");
        Assert.Equal(features.PropertyNames().Order(StringComparer.Ordinal), score.PerFeaturePct.Keys.Order(StringComparer.Ordinal));
        foreach (var key in features.PropertyNames())
        {
            Assert.Equal(features.Property(key).AsNumber(), score.PerFeaturePct[key], Tolerance);
        }
    }

    [Fact]
    public void RegionsEmbeddedInTheLibraryEqualThoseTheOracleUsed()
    {
        foreach (var project in OracleFixture.Document.Property("regions").PropertyNames())
        {
            var expected = FeatureRegions.Read(OracleFixture.Document.Property("regions").Property(project));

            Assert.Equal(expected, FeatureRegions.For(project));
        }
    }

    private static StrictJson Entry(string id) => OracleFixture.Document.Property("metrics").Items().Single(item => item.Property("id").AsString() == id);

    private static RgbImage Reference(StrictJson entry) => RasterPipeline.LoadNormalised(OracleFixture.RepoBytes(entry.Property("reference").AsString()));

    private static RgbImage Rendered(StrictJson entry) => RasterPipeline.Normalise(OracleFixture.RenderPixels(entry.Property("render_id").AsString()));
}
