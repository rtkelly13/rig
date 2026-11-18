using System.CommandLine;
using System.Text.Json;
using Rig.Cli.Image;
using Rig.Core.Tests;
using Rig.Image;

namespace Rig.Cli.Tests.Image;

/// <summary><c>rig image cross-check</c> through its command line, with scripted renderers standing in for Chromium and Svg.Skia.</summary>
public sealed class ImageCommandTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private Func<string?, Task<ISvgRenderer>> _authoritative = _ => Task.FromResult<ISvgRenderer>(new ScriptedRenderer("Chromium", (_, size) => Solid(size, 200)));
    private Func<ISvgRenderer> _fast = () => new ScriptedRenderer("Svg.Skia", (_, size) => Solid(size, 200));

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Agreeing_renderers_exit_0_and_say_verdicts_were_not_compared_without_candidates()
    {
        var exit = await Run("image", "cross-check");

        Assert.Equal(0, exit);
        var text = _out.ToString();
        Assert.Contains("Cross-check: Chromium (authoritative) vs Svg.Skia", text, StringComparison.Ordinal);
        Assert.Contains("rtkelly-labs.master.500", text, StringComparison.Ordinal);
        Assert.Contains("parquet-source-generator.favicon.16", text, StringComparison.Ordinal);
        Assert.Contains("Promotion verdicts not evaluated", text, StringComparison.Ordinal);
        Assert.Contains("Within thresholds.", text, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _err.ToString());
    }

    [Fact]
    public async Task Renderers_that_disagree_beyond_the_thresholds_exit_1_naming_each_breach()
    {
        _fast = () => new ScriptedRenderer("Svg.Skia", (_, size) => Solid(size, 100));

        var exit = await Run("image", "cross-check");

        Assert.Equal(1, exit);
        var text = _out.ToString();
        Assert.Contains("BREACH mean abs difference 100 exceeds 1", text, StringComparison.Ordinal);
        Assert.Contains("BREACH differing pixels 100% exceeds 20%", text, StringComparison.Ordinal);
        Assert.Contains("BREACH RMSE differs by", text, StringComparison.Ordinal);
        Assert.Contains("BREACH SSIM differs by", text, StringComparison.Ordinal);
        Assert.Contains("BREACH composite differs by", text, StringComparison.Ordinal);
        Assert.Contains("Thresholds broken.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_renderer_that_will_not_start_exits_2_with_the_reason()
    {
        _authoritative = _ => throw new RendererUnavailableException("expected Chromium to start, it did not: no browser installed");

        var exit = await Run("image", "cross-check");

        Assert.Equal(2, exit);
        Assert.Contains("Error: expected Chromium to start, it did not: no browser installed", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, _out.ToString());
    }

    [Fact]
    public async Task The_chromium_option_is_handed_to_the_launcher()
    {
        string? launched = null;
        _authoritative = executable =>
        {
            launched = executable;
            return Task.FromResult<ISvgRenderer>(new ScriptedRenderer("Chromium", (_, size) => Solid(size, 200)));
        };

        _ = await Run("image", "cross-check", "--chromium", "/opt/chrome");

        Assert.Equal("/opt/chrome", launched);
    }

    [Fact]
    public async Task The_report_option_writes_the_result_as_json()
    {
        var report = Path.Combine(_temp.Path, "out", "cross-check.json");

        var exit = await Run("image", "cross-check", "--report", report);

        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken));
        Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.False(json.RootElement.GetProperty("verdictsEvaluated").GetBoolean());
        Assert.Equal("Chromium", json.RootElement.GetProperty("authoritative").GetString());
        Assert.Equal(16, json.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(1.0, json.RootElement.GetProperty("thresholds").GetProperty("meanAbsDifference").GetDouble());
        var master = json.RootElement.GetProperty("files")[0];
        Assert.Equal("rtkelly-labs.master.2000", master.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Object, master.GetProperty("metrics").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("files")[1].GetProperty("metrics").ValueKind);
    }

    [Fact]
    public async Task A_verdict_the_fast_renderer_flips_fails_the_check_even_when_every_pixel_is_within_thresholds()
    {
        var candidates = _temp.Folder("candidates");
        await File.WriteAllTextAsync(Path.Combine(candidates, "rtkelly-labs.perfect.svg"), "<svg/>", TestContext.Current.CancellationToken);
        var reference = RasterPipeline.Decode(await File.ReadAllBytesAsync(Path.Combine(RepoRoot, CrossCheck.ReferencePath("rtkelly-labs")), TestContext.Current.CancellationToken));
        _authoritative = _ => Task.FromResult<ISvgRenderer>(new ScriptedRenderer("Chromium", (_, size) => Solid(size, 200)));
        _fast = () => new ScriptedRenderer("Svg.Skia", (path, size) => path.EndsWith("perfect.svg", StringComparison.Ordinal) ? reference : Solid(size, 200));

        var exit = await Run("image", "cross-check", "--candidates", candidates);

        Assert.Equal(1, exit);
        var text = _out.ToString();
        Assert.Contains("verdict rtkelly-labs.perfect: Chromium rejects, Svg.Skia promotes  FLIPPED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Promotion verdicts not evaluated", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_candidate_that_is_not_named_project_dot_candidate_is_an_error()
    {
        var candidates = _temp.Folder("candidates");
        await File.WriteAllTextAsync(Path.Combine(candidates, "stray.svg"), "<svg/>", TestContext.Current.CancellationToken);

        var exit = await Run("image", "cross-check", "--candidates", candidates);

        Assert.Equal(2, exit);
        Assert.Contains("expected a name like <project>.<candidate>.svg, found stray.svg", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_group_without_a_verb_is_a_usage_error()
    {
        var exit = await Run("image");

        Assert.NotEqual(0, exit);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rig.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("expected rig.slnx above the test output, found none");
    }

    private static Raster Solid(int size, byte value)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);
        return new Raster(size, size, pixels);
    }

    private Task<int> Run(params string[] args)
    {
        var host = new ImageHost(RepoRoot, _temp.Path, _authoritative, _fast);
        return new ImageCommand(host, _out, _err).Build().Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private sealed class ScriptedRenderer(string name, Func<string, int, Raster> render) : ISvgRenderer
    {
        public string Name => name;

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken) => Task.FromResult(render(svgPath, size));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
