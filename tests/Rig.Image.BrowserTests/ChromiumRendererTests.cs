using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Rig.Image.Tests;

namespace Rig.Image.BrowserTests;

public sealed class ChromiumRendererTests(ChromiumFixture chromium) : IClassFixture<ChromiumFixture>
{
    [Fact]
    public void BrowserIsThePinnedRevision()
    {
        Assert.Equal(ChromiumRenderer.PinnedBrowserVersion, chromium.Renderer.BrowserVersion);
        Assert.Equal(OracleFixture.Document.Property("provenance").Property("browser").AsString(), chromium.Renderer.BrowserVersion);
    }

    [Fact]
    public async Task RendersMatchTheOracleExactlyOnThePlatformItWasFrozenOnAndWithinThresholdsElsewhere()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var sameMachine = FrozenOnThisPlatform();
        var compared = 0;
        foreach (var render in OracleFixture.Document.Property("renders").Items())
        {
            var svg = render.Property("svg").AsString();
            var path = svg.StartsWith("candidates/", StringComparison.Ordinal) ? OracleFixture.PathOf(svg) : Path.Combine(OracleFixture.RepoRoot, svg);
            var size = render.Property("size").AsInteger();

            var raster = await chromium.Renderer.RenderAsync(path, size, cancellationToken);

            var hash = Convert.ToHexStringLower(SHA256.HashData(raster.Pixels));
            if (sameMachine)
            {
                Assert.True(render.Property("rgba_sha256").AsString() == hash, $"{render.Property("id").AsString()}: expected the frozen pixels, found a different render");
                compared++;
            }
            else if (render.Property("file").AsStringOrNull() is { } file)
            {
                var frozen = RasterPipeline.Decode(OracleFixture.Bytes(file));
                var (meanAbs, differing) = Difference(frozen, raster);
                Assert.True(meanAbs <= DivergenceThresholds.Adr.MeanAbsDifference, $"{render.Property("id").AsString()}: mean abs {meanAbs}");
                Assert.True(differing <= DivergenceThresholds.Adr.DifferingPixelFraction, $"{render.Property("id").AsString()}: differing {differing}");
                compared++;
            }
        }

        Assert.True(compared > 0, "expected at least one render compared, found none");
    }

    [Fact]
    public async Task ChromiumAndSvgSkiaStayWithinTheAdrThresholdsOnTheRealMasters()
    {
        await using var skia = new SvgSkiaRenderer();

        var report = await CrossCheck.RunAsync(
            chromium.Renderer,
            skia,
            OracleFixture.RepoRoot,
            CrossCheck.Files(),
            OracleFixture.PathOf("candidates"),
            DivergenceThresholds.Adr,
            TestContext.Current.CancellationToken);

        Assert.All(report.Files, file => Assert.True(file.Breaches.Count == 0, $"{file.File.Id}: {string.Join("; ", file.Breaches)}"));
        Assert.All(report.Verdicts, verdict => Assert.False(verdict.Flipped, $"{verdict.Project}.{verdict.Candidate} flipped"));
        Assert.Equal(CrossCheck.Files().Count, report.Files.Count);
        Assert.Equal(5, report.Verdicts.Count);
        Assert.True(report.Passed);
    }

    private static bool FrozenOnThisPlatform()
    {
        var provenance = OracleFixture.Document.Property("provenance");
        var os = provenance.Property("os").AsString();
        var arch = provenance.Property("arch").AsString();
        var osMatches = os switch
        {
            "Darwin" => OperatingSystem.IsMacOS(),
            "Linux" => OperatingSystem.IsLinux(),
            "Windows" => OperatingSystem.IsWindows(),
            _ => false,
        };
        var archMatches = arch switch
        {
            "arm64" => RuntimeInformation.OSArchitecture == Architecture.Arm64,
            "x86_64" or "AMD64" => RuntimeInformation.OSArchitecture == Architecture.X64,
            _ => false,
        };
        return osMatches && archMatches;
    }

    private static (double MeanAbs, double Differing) Difference(Raster a, Raster b)
    {
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        var left = a.Pixels;
        var right = b.Pixels;
        long total = 0;
        var differing = 0;
        for (var pixel = 0; pixel < left.Length; pixel += 4)
        {
            var any = false;
            for (var c = 0; c < 4; c++)
            {
                var diff = Math.Abs(left[pixel + c] - right[pixel + c]);
                total += diff;
                any |= diff != 0;
            }

            if (any)
            {
                differing++;
            }
        }

        return ((double)total / left.Length, (double)differing / (left.Length / 4));
    }
}
