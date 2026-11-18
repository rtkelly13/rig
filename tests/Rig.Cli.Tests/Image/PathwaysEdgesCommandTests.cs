using System.CommandLine;
using Rig.Cli.Image;
using Rig.Core.Tests;
using Rig.Image;

namespace Rig.Cli.Tests.Image;

/// <summary><c>rig image edges</c> and <c>rig image pathways</c> through their command lines, with Chromium unavailable so only the geometry is reported.</summary>
public sealed class PathwaysEdgesCommandTests : IDisposable
{
    // A square a few degrees off axis plus a micro segment, so the report has defects to list.
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><polygon points=\"10,10 90,13 90,90 10,90 10,12\" fill=\"#123456\"/></svg>";

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private Func<string?, Task<ISvgRenderer>> _authoritative = _ => throw new RendererUnavailableException("no browser in tests");

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Edges_reports_conformance_and_the_defect_counts()
    {
        var svg = Write("mark.svg");

        var exit = await Run("image", "edges", "--svg", svg);

        Assert.Equal(0, exit);
        var text = _out.ToString();
        Assert.Contains("Axis conformance", text, StringComparison.Ordinal);
        Assert.Contains("Total edges", text, StringComparison.Ordinal);
        Assert.Contains("Worst offenders", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edges_below_the_required_conformance_exits_one()
    {
        var svg = Write("mark.svg");

        var exit = await Run("image", "edges", "--svg", svg, "--min-conformance", "100");

        Assert.Equal(1, exit);
        Assert.Contains("is below the required 100.00%", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Edges_fix_writes_a_regularized_copy_and_promotes_nothing()
    {
        var svg = Write("mark.svg");
        var fixedPath = Path.Join(_temp.Path, "out", "fixed.svg");

        var exit = await Run("image", "edges", "--svg", svg, "--fix", fixedPath);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(fixedPath));
        Assert.Equal(Svg, await File.ReadAllTextAsync(svg, TestContext.Current.CancellationToken));
        Assert.Contains("Nothing was promoted", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("Drift not scored", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--axes", "0,abc")]
    [InlineData("--axes", " , ")]
    [InlineData("--project", "nope")]
    public async Task Edges_bad_options_exit_two_naming_the_value(string option, string value)
    {
        var svg = Write("mark.svg");

        var exit = await Run("image", "edges", "--svg", svg, option, value);

        Assert.Equal(2, exit);
        Assert.Contains("expected", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pathways_builds_the_requested_ones_and_skips_the_search_without_a_renderer()
    {
        var assets = Path.Join(_temp.Path, "assets", "rtkelly-labs");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Join(assets, "rtkelly-labs.svg"), Svg, TestContext.Current.CancellationToken);
        var outDir = Path.Join(_temp.Path, "candidates");

        var exit = await Run("image", "pathways", "--only", "traced,regularized,parametric,fitted", "--out-dir", outDir);

        Assert.Equal(0, exit);
        var text = _out.ToString();
        Assert.Contains("Chromium unavailable", text, StringComparison.Ordinal);
        Assert.Contains("Skipping 'fitted': needs a renderer.", text, StringComparison.Ordinal);
        Assert.Contains("Regularized trace", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Join(outDir, "rtkelly-labs-parametric.svg")));
        Assert.False(File.Exists(Path.Join(outDir, "rtkelly-labs-fitted.svg")));
    }

    [Fact]
    public async Task Pathways_autotraced_without_a_tracer_is_an_error()
    {
        var assets = Path.Join(_temp.Path, "assets", "rtkelly-labs");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Join(assets, "rtkelly-labs.svg"), Svg, TestContext.Current.CancellationToken);

        var exit = await Run("image", "pathways", "--only", "autotraced");

        Assert.Equal(2, exit);
        Assert.Contains("Error:", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pathways_unknown_key_lists_the_choices()
    {
        var exit = await Run("image", "pathways", "--only", "nope");

        Assert.Equal(2, exit);
        Assert.Contains("expected pathways from traced, autotraced, regularized, parametric, fitted, found nope", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pathways_with_a_renderer_scores_every_candidate_including_the_fitted_search()
    {
        var project = Path.Join(_temp.Path, "assets", "rtkelly-labs");
        Directory.CreateDirectory(Path.Join(project, "reference"));
        await File.WriteAllTextAsync(Path.Join(project, "rtkelly-labs.svg"), Svg, TestContext.Current.CancellationToken);
        var pixels = new byte[8 * 8 * 4];
        Array.Fill(pixels, (byte)255);
        await File.WriteAllBytesAsync(Path.Join(project, "reference", "rtkelly-labs.png"), RasterPipeline.EncodePng(new Raster(8, 8, pixels)), TestContext.Current.CancellationToken);
        var rendered = new List<string>();
        _authoritative = _ => Task.FromResult<ISvgRenderer>(new Grey(rendered));

        var exit = await Run("image", "pathways", "--only", "traced,parametric,fitted");

        Assert.Equal(0, exit);
        var text = _out.ToString();
        Assert.Contains("SSIM", text, StringComparison.Ordinal);
        Assert.Contains("Composite", text, StringComparison.Ordinal);
        Assert.Contains("Traced", text, StringComparison.Ordinal);
        Assert.Contains("Fitted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Skipping", text, StringComparison.Ordinal);
        Assert.Contains("pathway-fitted", rendered[^1], StringComparison.Ordinal);
        Assert.True(rendered.Count(p => p.EndsWith("fit.svg", StringComparison.Ordinal)) > 10, "the fitted search should have scored many neighbours");
    }

    [Fact]
    public async Task Edges_fix_scores_the_drift_before_and_after_with_a_renderer()
    {
        var project = Path.Join(_temp.Path, "assets", "rtkelly-labs");
        Directory.CreateDirectory(Path.Join(project, "reference"));
        var pixels = new byte[8 * 8 * 4];
        Array.Fill(pixels, (byte)255);
        await File.WriteAllBytesAsync(Path.Join(project, "reference", "rtkelly-labs.png"), RasterPipeline.EncodePng(new Raster(8, 8, pixels)), TestContext.Current.CancellationToken);
        _authoritative = _ => Task.FromResult<ISvgRenderer>(new Grey([]));
        var svg = Write("mark.svg");

        var exit = await Run("image", "edges", "--svg", svg, "--fix", Path.Join(_temp.Path, "fixed.svg"));

        Assert.Equal(0, exit);
        Assert.Contains("Drift vs reference: SSIM", _out.ToString(), StringComparison.Ordinal);
    }

    private string Write(string name)
    {
        var path = Path.Join(_temp.Path, name);
        File.WriteAllText(path, Svg);
        return path;
    }

    private sealed class Grey(List<string> rendered) : ISvgRenderer
    {
        public string Name => "Grey";

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
        {
            rendered.Add(svgPath);
            var pixels = new byte[size * size * 4];
            Array.Fill(pixels, (byte)200);
            return Task.FromResult(new Raster(size, size, pixels));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private Task<int> Run(params string[] args)
    {
        var host = new ImageHost(
            _temp.Path,
            Path.Join(_temp.Path, "scratch"),
            _authoritative,
            () => throw new InvalidOperationException("the fast renderer is never used"));
        return new ImageCommand(host, _out, _err).Build().Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }
}
