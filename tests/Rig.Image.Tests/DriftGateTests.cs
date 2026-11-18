using System.Text;

namespace Rig.Image.Tests;

public sealed class DriftGateTests : IDisposable
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M 0 0 L 5 0.01 L 10 0 L 10 10 Z\" fill=\"#ff0000\"/></svg>";

    private readonly string _dir = Path.Join(Path.GetTempPath(), $"rig-gate-{Guid.NewGuid():N}");

    public DriftGateTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task A_candidate_that_renders_like_the_original_passes_on_the_first_try_and_is_written()
    {
        var input = Write("in.svg");
        var output = Path.Join(_dir, "nested", "out.svg");
        await using var renderer = new Scripted((_, _) => 100);

        var gated = await new DriftGate(renderer, Path.Join(_dir, "scratch")).OptimizeAsync(input, output, 0.5, 0.995, 1.0, TestContext.Current.CancellationToken);

        Assert.True(gated.Report.GatePassed);
        Assert.Equal(output, gated.Path);
        Assert.Equal(gated.Report.OutputSvg, await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal(0.5, gated.Report.Tolerance);
        Assert.Equal(2, renderer.Renders);
        Assert.Equal(1.0, gated.Report.Ssim);
        Assert.Equal(0.0, gated.Report.RmsePct);
        Assert.Equal(Encoding.UTF8.GetByteCount(Svg), gated.Report.OriginalSizeBytes);
        Assert.Equal(1, gated.Report.OriginalNodeCount - gated.Report.FinalNodeCount);
        Assert.Empty(Directory.GetDirectories(Path.Join(_dir, "scratch")));
    }

    [Fact]
    public async Task A_missed_budget_backs_the_tolerance_off_and_keeps_the_best_candidate()
    {
        var input = Write("in.svg");
        var output = Path.Join(_dir, "out.svg");

        // The baseline is white; candidates get lighter grey the later they are, so the last is the best.
        await using var renderer = new Scripted((path, call) => Path.GetFileName(path).StartsWith("candidate_", StringComparison.Ordinal) ? 100 + (call * 30) : 255);
        var gated = await new DriftGate(renderer, _dir).OptimizeAsync(input, output, 0.5, 0.9999, 0.0, TestContext.Current.CancellationToken);

        Assert.False(gated.Report.GatePassed);
        Assert.Equal(0.0625, gated.Report.Tolerance);
        Assert.Equal(5, renderer.Renders);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task A_missed_budget_refuses_to_overwrite_the_input()
    {
        var input = Write("in.svg");
        await using var renderer = new Scripted((path, _) => Path.GetFileName(path).StartsWith("candidate_", StringComparison.Ordinal) ? 0 : 255);

        var exception = await Assert.ThrowsAsync<DriftGateNotMetException>(
            () => new DriftGate(renderer, _dir).OptimizeAsync(input, null, 0.5, 0.995, 1.0, TestContext.Current.CancellationToken));

        Assert.Contains("did not meet the drift budget", exception.Message, StringComparison.Ordinal);
        Assert.Contains("< 0.995", exception.Message, StringComparison.Ordinal);
        Assert.Contains("> 1.0%", exception.Message, StringComparison.Ordinal);
        Assert.Equal(Svg, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_passing_run_may_overwrite_the_input()
    {
        var input = Write("in.svg");

        await using var renderer = new Scripted((_, _) => 50);
        var gated = await new DriftGate(renderer, _dir).OptimizeAsync(input, null, 0.5, 0.995, 1.0, TestContext.Current.CancellationToken);

        Assert.Equal(input, gated.Path);
        Assert.Equal(gated.Report.OutputSvg, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_empty_document_reports_no_reduction()
    {
        var input = Path.Join(_dir, "empty.svg");
        await File.WriteAllTextAsync(input, string.Empty, TestContext.Current.CancellationToken);

        await using var renderer = new Scripted((_, _) => 50);
        var gated = await new DriftGate(renderer, _dir).OptimizeAsync(input, Path.Join(_dir, "o.svg"), 0.5, 0.995, 1.0, TestContext.Current.CancellationToken);

        Assert.Equal(0.0, gated.Report.SizeReductionPct);
        Assert.Equal(0.0, gated.Report.NodeReductionPct);
    }

    private string Write(string name)
    {
        var path = Path.Join(_dir, name);
        File.WriteAllText(path, Svg);
        return path;
    }

    private sealed class Scripted(Func<string, int, int> grey) : ISvgRenderer
    {
        public int Renders { get; private set; }

        public string Name => "scripted";

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
        {
            var call = Renders++;
            var pixels = new byte[size * size * 4];
            Array.Fill(pixels, (byte)grey(svgPath, call));
            return Task.FromResult(new Raster(size, size, pixels));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
