using System.CommandLine;
using Rig.Cli.Image;
using Rig.Core.Tests;
using Rig.Image;

namespace Rig.Cli.Tests.Image;

/// <summary><c>rig image simplify</c> through its command line, with scripted renderers standing in for Svg.Skia and Chromium.</summary>
public sealed class SimplifyCommandTests : IDisposable
{
    private const string Svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M 0 0 L 5 0.01 L 10 0 L 10 10 Z\" fill=\"#ff0000\"/></svg>";

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private Func<string?, Task<ISvgRenderer>> _authoritative = _ => Task.FromResult<ISvgRenderer>(new Scripted("Chromium", _ => 200));
    private Func<ISvgRenderer> _fast = () => new Scripted("Svg.Skia", _ => 200);

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task A_met_budget_writes_the_sidecar_and_prints_the_table()
    {
        var input = Write("logo.svg");

        var exit = await Run("image", "simplify", input);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Join(_temp.Path, "logo.optimized.svg")));
        Assert.Equal(Svg, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
        var text = _out.ToString();
        Assert.Contains("Path node count", text, StringComparison.Ordinal);
        Assert.Contains("PASS", text, StringComparison.Ordinal);
        Assert.Contains("Wrote optimized SVG to:", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_explicit_output_is_used()
    {
        var input = Write("logo.svg");
        var output = Path.Join(_temp.Path, "sub", "small.svg");

        var exit = await Run("image", "simplify", input, "--output", output);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task In_place_overwrites_the_input()
    {
        var input = Write("logo.svg");

        var exit = await Run("image", "simplify", input, "--in-place");

        Assert.Equal(0, exit);
        Assert.NotEqual(Svg, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missed_budget_with_an_output_writes_the_candidate_and_exits_1()
    {
        var input = Write("logo.svg");
        _fast = () => new Scripted("Svg.Skia", path => Path.GetFileName(path).StartsWith("candidate_", StringComparison.Ordinal) ? 0 : 255);

        var exit = await Run("image", "simplify", input);

        Assert.Equal(1, exit);
        Assert.True(File.Exists(Path.Join(_temp.Path, "logo.optimized.svg")));
        Assert.Contains("Drift gate not met after backoff", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("WARN", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missed_budget_in_place_exits_1_and_leaves_the_input()
    {
        var input = Write("logo.svg");
        _fast = () => new Scripted("Svg.Skia", path => Path.GetFileName(path).StartsWith("candidate_", StringComparison.Ordinal) ? 0 : 255);

        var exit = await Run("image", "simplify", input, "-i");

        Assert.Equal(1, exit);
        Assert.Equal(Svg, await File.ReadAllTextAsync(input, TestContext.Current.CancellationToken));
        Assert.Contains("Drift gate not met: Simplification of", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_exits_2()
    {
        var exit = await Run("image", "simplify", Path.Join(_temp.Path, "none.svg"));

        Assert.Equal(2, exit);
        Assert.Contains("found none", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_document_the_simplifier_cannot_read_exits_2()
    {
        var input = Path.Join(_temp.Path, "bad.svg");
        await File.WriteAllTextAsync(input, "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1.2.3\" height=\"4\"><rect width=\"1\" height=\"1\"/></svg>", TestContext.Current.CancellationToken);

        var exit = await Run("image", "simplify", input);

        Assert.Equal(2, exit);
        Assert.Contains("could not simplify", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_view_box_exits_2()
    {
        var input = Path.Join(_temp.Path, "box.svg");
        await File.WriteAllTextAsync(input, "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 x 4\"><rect width=\"1\" height=\"1\"/></svg>", TestContext.Current.CancellationToken);

        var exit = await Run("image", "simplify", input);

        Assert.Equal(2, exit);
        Assert.Contains("could not simplify", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_the_renderer_cannot_load_exits_2()
    {
        var input = Write("logo.svg");
        _fast = () => new Scripted("Svg.Skia", _ => throw new InvalidDataException("expected an SVG Svg.Skia can load, found one it cannot"));

        var exit = await Run("image", "simplify", input);

        Assert.Equal(2, exit);
        Assert.Contains("Error: expected an SVG", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_chromium_uses_the_authoritative_renderer_with_the_executable()
    {
        var input = Write("logo.svg");
        string? launched = null;
        _authoritative = executable =>
        {
            launched = executable;
            return Task.FromResult<ISvgRenderer>(new Scripted("Chromium", _ => 200));
        };
        _fast = () => throw new InvalidOperationException("the fast renderer must not be used");

        var exit = await Run("image", "simplify", input, "--chromium", "/opt/chrome");

        Assert.Equal(0, exit);
        Assert.Equal("/opt/chrome", launched);
    }

    [Fact]
    public async Task The_authoritative_flag_uses_the_bundled_browser()
    {
        var input = Write("logo.svg");
        var launched = false;
        _authoritative = executable =>
        {
            launched = executable is null;
            return Task.FromResult<ISvgRenderer>(new Scripted("Chromium", _ => 200));
        };

        var exit = await Run("image", "simplify", input, "--authoritative");

        Assert.Equal(0, exit);
        Assert.True(launched);
    }

    [Fact]
    public async Task A_renderer_that_will_not_start_exits_2_with_the_reason()
    {
        var input = Write("logo.svg");
        _authoritative = _ => throw new RendererUnavailableException("Chromium is not installed");

        var exit = await Run("image", "simplify", input, "--authoritative");

        Assert.Equal(2, exit);
        Assert.Contains("Error: Chromium is not installed", _err.ToString(), StringComparison.Ordinal);
    }

    private string Write(string name)
    {
        var path = Path.Join(_temp.Path, name);
        File.WriteAllText(path, Svg);
        return path;
    }

    private Task<int> Run(params string[] args)
    {
        var host = new ImageHost(_temp.Path, Path.Join(_temp.Path, "scratch"), _authoritative, _fast);
        return new ImageCommand(host, _out, _err).Build().Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    private sealed class Scripted(string name, Func<string, int> grey) : ISvgRenderer
    {
        public string Name => name;

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
        {
            var pixels = new byte[size * size * 4];
            Array.Fill(pixels, (byte)grey(svgPath));
            return Task.FromResult(new Raster(size, size, pixels));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
