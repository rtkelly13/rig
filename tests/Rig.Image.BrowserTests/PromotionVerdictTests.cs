using System.Text;
using Rig.Image.Pathways;
using Rig.Image.Tests;

namespace Rig.Image.BrowserTests;

/// <summary>Every candidate the C# pathways generate, scored under real Chromium, gets the verdict the frozen Python oracle recorded.</summary>
public sealed class PromotionVerdictTests(ChromiumFixture chromium) : IClassFixture<ChromiumFixture>
{
    [Fact]
    public async Task C_sharp_generated_candidates_get_the_oracle_verdicts_under_chromium()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var folder = Path.Combine(Path.GetTempPath(), "rig-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var project in new[] { "rtkelly-labs", "parquet-source-generator" })
            {
                var master = project == "rtkelly-labs" ? project : "parquet-diamond";
                var masterText = Encoding.UTF8.GetString(OracleFixture.RepoBytes($"assets/{project}/{master}.svg"));
                await File.WriteAllTextAsync(Path.Combine(folder, $"{project}.regularized.svg"), EdgeRegularity.RegularizeSvg(masterText), cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(folder, $"{project}.autotraced.svg"), OracleFixture.Bytes($"candidates/{project}.autotraced.svg"), cancellationToken);
            }

            await File.WriteAllTextAsync(Path.Combine(folder, "rtkelly-labs.parametric.svg"), IsoLoopBuilder.GenerateSvg(), cancellationToken);
            await using var skia = new SvgSkiaRenderer();

            var report = await CrossCheck.RunAsync(chromium.Renderer, skia, OracleFixture.RepoRoot, [], folder, DivergenceThresholds.Adr, cancellationToken);

            var expected = OracleFixture.Document.Property("promotion").Items().ToDictionary(
                entry => $"{entry.Property("project").AsString()}.{entry.Property("candidate").AsString()}",
                entry => entry.Property("promotable").AsBoolean());
            Assert.Equal(expected.Count, report.Verdicts.Count);
            foreach (var verdict in report.Verdicts)
            {
                Assert.True(expected[$"{verdict.Project}.{verdict.Candidate}"] == verdict.AuthoritativePromotable, $"{verdict.Project}.{verdict.Candidate}: expected the oracle's verdict, found the opposite");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
