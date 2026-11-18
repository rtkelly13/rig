using System.CommandLine;
using Rig.Cli.Image;
using Rig.Core.Tests;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Tests.Image;

/// <summary>The build, compare, diagnose, export, snapshot, vectorize, contact-sheet and preview commands over a scratch repository and a grey renderer.</summary>
public sealed class ImageToolCommandsTests : IDisposable
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#808080\"/></svg>";

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private Func<string?, Task<ISvgRenderer>> _authoritative = _ => Task.FromResult<ISvgRenderer>(new Grey(200));
    private IVtracer? _tracer;

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Build_lists_the_variants_and_marks_the_default()
    {
        var exit = await Run("image", "build", "--list-variants");

        Assert.Equal(0, exit);
        Assert.Contains("master (default)", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_writes_the_builders_bytes_where_asked_and_the_default_variant_otherwise()
    {
        var target = Path.Join(_temp.Path, "out", "mark.svg");

        Assert.Equal(0, await Run("image", "build", "--svg", target));
        Assert.Equal(LabsBuilder.GenerateSvg(), await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));

        Assert.Equal(0, await Run("image", "build", "--project", "parquet"));
        var master = Path.Join(_temp.Path, "assets", "parquet-source-generator", "parquet-diamond.svg");
        Assert.Equal(DiamondParquetBuilder.GenerateSvg(), await File.ReadAllTextAsync(master, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Build_an_unknown_variant_exits_two_naming_the_declared_ones()
    {
        var exit = await Run("image", "build", "--variant", "nope");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown variant 'nope'. Declared variants:", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractFeature_crops_a_raster_and_renders_an_svg_first()
    {
        await WriteReference();
        var crop = Path.Join(_temp.Path, "crop.png");
        var fromSvg = Path.Join(_temp.Path, "svg-crop.png");

        Assert.Equal(0, await Run("image", "extract-feature", "--image", ReferencePath, "--feature", "crossover_core", "--output", crop));
        Assert.Equal(0, await Run("image", "extract-feature", "--image", WriteFile("mark.svg", Svg), "--feature", "crossover_core", "--output", fromSvg));

        var box = FeatureRegions.For("rtkelly-labs").First(b => b.Key == "crossover_core");
        var decoded = RasterPipeline.Decode(await File.ReadAllBytesAsync(crop, TestContext.Current.CancellationToken));
        Assert.Equal(box.X1 - box.X0, decoded.Width);
        Assert.Equal(box.Y1 - box.Y0, decoded.Height);
        Assert.True(File.Exists(fromSvg));
    }

    [Fact]
    public async Task ExtractFeature_an_unknown_feature_exits_two()
    {
        await WriteReference();

        var exit = await Run("image", "extract-feature", "--image", ReferencePath, "--feature", "nope", "--output", Path.Join(_temp.Path, "x.png"));

        Assert.Equal(2, exit);
        Assert.Contains("expected one of crossover_core", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractFeature_an_unknown_project_exits_two()
    {
        await WriteReference();

        var exit = await Run("image", "extract-feature", "--image", ReferencePath, "--feature", "crossover_core", "--project", "nope", "--output", Path.Join(_temp.Path, "x.png"));

        Assert.Equal(2, exit);
        Assert.Contains("found 'nope'", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractFeature_a_missing_image_exits_two()
    {
        var exit = await Run("image", "extract-feature", "--image", Path.Join(_temp.Path, "absent.png"), "--feature", "crossover_core", "--output", Path.Join(_temp.Path, "x.png"));

        Assert.Equal(2, exit);
        Assert.Contains("expected an image file at", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_reports_the_drift_the_worst_regions_and_each_feature()
    {
        await WriteReference();
        WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);

        var exit = await Run("image", "compare", "--decompose");

        Assert.Equal(0, exit);
        var text = _out.ToString();
        Assert.Contains("Drift Summary - rtkelly-labs", text, StringComparison.Ordinal);
        Assert.Contains("RMSE", text, StringComparison.Ordinal);
        Assert.Contains("Top Spatial Drift Regions", text, StringComparison.Ordinal);
        Assert.Contains("Per-Feature Drift", text, StringComparison.Ordinal);
        Assert.Contains("crossover_core", text, StringComparison.Ordinal);
        Assert.Contains("ACTIONABLE_BUILDER_REFINEMENTS", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_a_feature_with_a_prerendered_image_writes_the_diff_and_the_sheet()
    {
        await WriteReference();
        var render = Path.Join(_temp.Path, "render.png");
        await File.WriteAllBytesAsync(render, RasterPipeline.EncodePng(Solid(16, 60)), TestContext.Current.CancellationToken);
        var diff = Path.Join(_temp.Path, "out", "diff.png");
        var sheet = Path.Join(_temp.Path, "out", "sheet.png");

        var exit = await Run("image", "compare", "--render", render, "--feature", "crossover_core", "--output-diff", diff, "--output-sheet", sheet);

        Assert.Equal(0, exit);
        var text = _out.ToString();
        Assert.Contains("(Isolated Feature: crossover_core)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Per-Feature Drift", text, StringComparison.Ordinal);
        Assert.True(File.Exists(diff));
        Assert.True(File.Exists(sheet));
    }

    [Fact]
    public async Task Compare_only_the_sheet_still_builds_the_heatmap_it_needs()
    {
        await WriteReference();
        WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);
        var sheet = Path.Join(_temp.Path, "sheet.png");

        Assert.Equal(0, await Run("image", "compare", "--output-sheet", sheet));
        Assert.True(File.Exists(sheet));
    }

    [Fact]
    public async Task Compare_missing_inputs_and_unknown_features_exit_two()
    {
        Assert.Equal(2, await Run("image", "compare"));
        Assert.Contains("expected a reference image at", _err.ToString(), StringComparison.Ordinal);

        await WriteReference();
        Assert.Equal(2, await Run("image", "compare", "--feature", "nope"));
        Assert.Equal(2, await Run("image", "compare", "--render", Path.Join(_temp.Path, "absent.png")));
        Assert.Contains("expected a render image at", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Run("image", "compare"));
        Assert.Contains("expected an SVG file at", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_with_no_browser_exits_two_with_the_reason()
    {
        await WriteReference();
        WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);
        _authoritative = _ => throw new RendererUnavailableException("no browser in tests");

        Assert.Equal(2, await Run("image", "compare"));
        Assert.Contains("Error: no browser in tests", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnose_prints_the_decomposed_diagnostics_at_the_requested_size()
    {
        await WriteReference();
        WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);

        Assert.Equal(0, await Run("image", "diagnose", "--size", "100"));
        Assert.Contains("DECOMPOSED_DRIFT_DIAGNOSTICS", _out.ToString(), StringComparison.Ordinal);

        var render = Path.Join(_temp.Path, "render.png");
        await File.WriteAllBytesAsync(render, RasterPipeline.EncodePng(Solid(8, 30)), TestContext.Current.CancellationToken);
        Assert.Equal(0, await Run("image", "diagnose", "--size", "100", "--render", render));
    }

    [Fact]
    public async Task Diagnose_a_tiny_size_exits_two()
    {
        Assert.Equal(2, await Run("image", "diagnose", "--size", "2"));
        Assert.Contains("expected --size of at least 5, found 2", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAvatar_writes_exactly_the_cuts_the_csv_declares()
    {
        WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);
        WriteFile(
            "assets/rtkelly-labs/production-cuts/production-cuts.csv",
            "filename,cut_type,width,height,format,purpose,description\nmaster.svg,master_svg,1024,1024,svg,a,b\navatar-64.png,avatar,64,64,png,a,b\nfavicon-32.png,favicon_32,32,32,png,a,b\n");
        WriteFile("assets/rtkelly-labs/production-cuts/stale.png", "not in the csv");

        var exit = await Run("image", "export-avatar");

        Assert.Equal(0, exit);
        var folder = Path.Join(_temp.Path, "assets", "rtkelly-labs", "production-cuts");
        Assert.Equal(Svg, await File.ReadAllTextAsync(Path.Join(folder, "master.svg"), TestContext.Current.CancellationToken));
        Assert.Equal(64, RasterPipeline.Decode(await File.ReadAllBytesAsync(Path.Join(folder, "avatar-64.png"), TestContext.Current.CancellationToken)).Width);
        Assert.Equal(32, RasterPipeline.Decode(await File.ReadAllBytesAsync(Path.Join(folder, "favicon-32.png"), TestContext.Current.CancellationToken)).Width);
        Assert.Contains("Exported 3 rtkelly-labs cuts to:", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAvatar_without_a_csv_or_a_master_exits_two()
    {
        Assert.Equal(2, await Run("image", "export-avatar"));
        Assert.NotEqual(string.Empty, _err.ToString());
    }

    [Fact]
    public async Task Snapshot_captures_the_given_svg_then_falls_back_to_the_projects_files()
    {
        var variations = Path.Join(_temp.Path, "variations");
        var explicitSvg = WriteFile("custom.svg", Svg);

        Assert.Equal(0, await Run("image", "snapshot", "--project", "rtkelly-labs", "--name", "first", "--svg", explicitSvg, "--variations-dir", variations, "--notes", "why"));
        Assert.Contains("Snapshot captured", _out.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Join(variations, "rtkelly-labs", "run-1"), "*.svg"));

        WriteFile("assets/parquet/other.svg", Svg);
        Assert.Equal(0, await Run("image", "snapshot", "--project", "parquet", "--name", "scan", "--variations-dir", variations, "--run-id", "run-2", "--step-idx", "2"));
        Assert.Single(Directory.GetFiles(Path.Join(variations, "parquet", "run-2"), "*.svg"));

        WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);
        Assert.Equal(0, await Run("image", "snapshot", "--project", "rtkelly-labs", "--name", "named", "--variations-dir", variations, "--run-id", "run-3"));
    }

    [Fact]
    public async Task Snapshot_with_nothing_to_capture_exits_two()
    {
        var exit = await Run("image", "snapshot", "--project", "ghost", "--name", "x");

        Assert.Equal(2, exit);
        Assert.Contains("Could not find an SVG to snapshot for project 'ghost'.", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vectorize_traces_scores_and_writes_the_sheet()
    {
        _tracer = new FixedTracer(Svg);
        var image = Path.Join(_temp.Path, "in.png");
        await File.WriteAllBytesAsync(image, RasterPipeline.EncodePng(Solid(16, 128)), TestContext.Current.CancellationToken);
        var output = Path.Join(_temp.Path, "out", "v.svg");
        var sheet = Path.Join(_temp.Path, "out", "sheet.png");

        var exit = await Run("image", "vectorize", "--image", image, "--output", output, "--output-sheet", sheet, "--mode", "spline");

        Assert.Equal(0, exit);
        Assert.Equal(Svg, await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Contains("Vectorization Scoring", _out.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(sheet));
        Assert.Equal("spline", ((FixedTracer)_tracer).Seen?.Mode);
    }

    [Fact]
    public async Task Vectorize_no_compare_stops_after_writing_and_errors_are_named()
    {
        var image = Path.Join(_temp.Path, "in.png");
        await File.WriteAllBytesAsync(image, RasterPipeline.EncodePng(Solid(16, 128)), TestContext.Current.CancellationToken);

        Assert.Equal(2, await Run("image", "vectorize", "--image", image));
        Assert.Contains("expected a VTracer to be wired in, found none", _err.ToString(), StringComparison.Ordinal);

        _tracer = new FixedTracer(Svg);
        Assert.Equal(0, await Run("image", "vectorize", "--image", image, "--output", Path.Join(_temp.Path, "n.svg"), "--no-compare"));
        Assert.DoesNotContain("Vectorization Scoring", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Run("image", "vectorize", "--image", Path.Join(_temp.Path, "absent.png")));
    }

    [Fact]
    public async Task ContactSheet_composes_the_reference_and_each_variant()
    {
        await WriteReference();
        var sheet = Path.Join(_temp.Path, "out", "sheet.png");

        var exit = await Run("image", "contact-sheet", "--feature", "crossover_core", "--variants", "master", "--cell", "64", "--output", sheet);

        Assert.Equal(0, exit);
        Assert.Contains("Contact sheet (crossover_core, 2 panels)", _out.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(sheet));
        Assert.Equal(0, await Run("image", "contact-sheet", "--cell", "32", "--output", sheet));
        Assert.Contains("full logo", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--feature", "nope", "expected one of crossover_core")]
    [InlineData("--variants", "master,nope", "found nope")]
    public async Task ContactSheet_unknown_choices_exit_two(string option, string value, string message)
    {
        await WriteReference();

        Assert.Equal(2, await Run("image", "contact-sheet", option, value, "--output", Path.Join(_temp.Path, "s.png")));
        Assert.Contains(message, _err.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("choose")]
    [InlineData("drift")]
    public async Task BuildPreview_writes_the_page_for_each_mode(string mode)
    {
        await WriteReference();
        var folder = Path.Join(_temp.Path, "preview");

        var exit = await Run("image", "build-preview", "--mode", mode, "--output-dir", folder, "--no-screenshot", "--variants", "master,favicon");

        Assert.Equal(0, exit);
        var html = await File.ReadAllTextAsync(Path.Join(folder, "preview.html"), TestContext.Current.CancellationToken);
        Assert.Contains("<html", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Join(folder, "screenshot.png")));
        Assert.Contains("Preview complete", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPreview_single_builds_the_target_page_without_writing_the_master()
    {
        await WriteReference();
        var master = WriteFile("assets/rtkelly-labs/rtkelly-labs.svg", Svg);
        var folder = Path.Join(_temp.Path, "preview");

        Assert.Equal(0, await Run("image", "build-preview", "--single", "--output-dir", folder, "--no-screenshot"));
        Assert.Equal(Svg, await File.ReadAllTextAsync(master, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Join(folder, "preview.html")));
    }

    [Fact]
    public async Task BuildPreview_a_screenshot_needs_a_browser_renderer()
    {
        await WriteReference();

        var exit = await Run("image", "build-preview", "--output-dir", Path.Join(_temp.Path, "preview"));

        Assert.Equal(2, exit);
        Assert.Contains("expected the authoritative renderer to be a browser", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPreview_bad_mode_and_variants_exit_two()
    {
        Assert.Equal(2, await Run("image", "build-preview", "--mode", "nope"));
        Assert.Contains("expected --mode choose or drift, found 'nope'", _err.ToString(), StringComparison.Ordinal);

        await WriteReference();
        Assert.Equal(2, await Run("image", "build-preview", "--variants", "nope"));
    }

    private string ReferencePath => Path.Join(_temp.Path, "assets", "rtkelly-labs", "reference", "rtkelly-labs.png");

    private static Raster Solid(int size, byte value)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);
        return new Raster(size, size, pixels);
    }

    private async Task WriteReference()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ReferencePath)!);
        await File.WriteAllBytesAsync(ReferencePath, RasterPipeline.EncodePng(Solid(16, 40)), TestContext.Current.CancellationToken);
    }

    private string WriteFile(string relative, string text)
    {
        var path = Path.Join(_temp.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private Task<int> Run(params string[] args)
    {
        var host = new ImageHost(_temp.Path, Path.Join(_temp.Path, "scratch"), _authoritative, () => throw new InvalidOperationException("the fast renderer is never used"), _tracer);
        return new ImageCommand(host, _out, _err).Build().Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private sealed class Grey(byte value) : ISvgRenderer
    {
        public string Name => "Grey";

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken) => Task.FromResult(Solid(size, value));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTracer(string svg) : IVtracer
    {
        public VtracerConfig? Seen { get; private set; }

        public Task<string> TraceAsync(string referencePath, VtracerConfig config, CancellationToken cancellationToken)
        {
            Seen = config;
            return Task.FromResult(svg);
        }
    }
}
