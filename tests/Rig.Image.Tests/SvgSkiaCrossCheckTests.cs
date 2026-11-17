namespace Rig.Image.Tests;

public sealed class SvgSkiaCrossCheckTests
{
    [Fact]
    public async Task SvgSkiaStaysWithinTheAdrThresholdsOfTheFrozenChromiumRenders()
    {
        var files = CrossCheck.Files().Where(file => file.Size <= 500).ToList();
        await using var skia = new SvgSkiaRenderer();
        await using var chromium = new FrozenRenderer();

        var report = await CrossCheck.RunAsync(chromium, skia, OracleFixture.RepoRoot, files, OracleFixture.PathOf("candidates"), DivergenceThresholds.Adr, TestContext.Current.CancellationToken);

        Assert.Equal(files.Count, report.Files.Count);
        Assert.All(report.Files, file => Assert.True(file.Breaches.Count == 0, $"{file.File.Id}: {string.Join("; ", file.Breaches)}"));
        Assert.All(report.Verdicts, verdict => Assert.False(verdict.Flipped, $"{verdict.Project}.{verdict.Candidate} flipped"));
        Assert.True(report.VerdictsEvaluated);
        Assert.Equal(5, report.Verdicts.Count);
    }
}
