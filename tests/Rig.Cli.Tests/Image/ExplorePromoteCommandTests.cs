using System.CommandLine;
using Rig.Cli.Image;
using Rig.Core.Tests;
using Rig.Image;
using Rig.Image.Pathways;

namespace Rig.Cli.Tests.Image;

/// <summary><c>rig image explore</c> and <c>rig image promote</c> through their command lines, with a scripted renderer in place of Chromium.</summary>
public sealed class ExplorePromoteCommandTests : IDisposable
{
    private const string Incumbent = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1024 1024\"><rect width=\"1024\" height=\"1024\" fill=\"#808080\"/></svg>";

    private readonly TempDir _temp = new();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private int _candidateGrey = 255;
    private Func<string?, Task<ISvgRenderer>>? _launch;

    public void Dispose()
    {
        _out.Dispose();
        _err.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public async Task Explore_scores_a_sweep_and_records_every_candidate_under_the_run()
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");

        var exit = await Run("image", "explore", "--run-id", "r1", "--sweep", "r_inner=60,70", "--variations-dir", variations);

        Assert.Equal(0, exit);
        var run = new SnapshotManager(variations).GetRun("rtkelly-labs", "r1");
        Assert.Equal(["incumbent", "candidate-1", "candidate-2"], run!.Steps.Select(s => s.StepName));
        Assert.Contains("PROMOTABLE", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("rig image promote -p rtkelly-labs --run-id r1", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(Incumbent, await File.ReadAllTextAsync(Path.Join(_temp.Path, "assets", "rtkelly-labs", "rtkelly-labs.svg"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Explore_exits_one_when_no_candidate_beats_the_incumbent()
    {
        await Arrange();
        _candidateGrey = 100;

        var exit = await Run("image", "explore", "--run-id", "r1", "--variations-dir", Path.Join(_temp.Path, "v"));

        Assert.Equal(1, exit);
        Assert.Contains("The master stands.", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--preset", "nope")]
    [InlineData("--param", "nope=1")]
    [InlineData("--param", "r_inner")]
    [InlineData("--project", "parquet")]
    [InlineData("--sweep", "r_inner=abc")]
    public async Task Explore_bad_options_exit_two(string option, string value)
    {
        await Arrange();

        var exit = await Run("image", "explore", option, value);

        Assert.Equal(2, exit);
        Assert.Contains("Error:", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explore_over_the_cap_exits_two()
    {
        await Arrange();

        var exit = await Run("image", "explore", "--sweep", "r_inner=60,70,80", "--max-candidates", "2");

        Assert.Equal(2, exit);
        Assert.Contains("expected at most 2 candidates, found a sweep of 3", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promote_without_confirm_is_a_dry_run_that_writes_nothing()
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);

        var exit = await Run("image", "promote", "--run-id", "r1", "--candidate", "1", "--variations-dir", variations);

        Assert.Equal(1, exit);
        Assert.Contains("Dry run.", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(Incumbent, await File.ReadAllTextAsync(Path.Join(_temp.Path, "assets", "rtkelly-labs", "rtkelly-labs.svg"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Promote_with_confirm_backs_up_the_incumbent_and_writes_the_master_and_manifest()
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);
        var candidate = new SnapshotManager(variations).GetRun("rtkelly-labs", "r1")!.Steps[1];

        var exit = await Run("image", "promote", "--run-id", "r1", "--candidate", "1", "--confirm", "--variations-dir", variations);

        Assert.Equal(0, exit);
        var cancellationToken = TestContext.Current.CancellationToken;
        var expected = await File.ReadAllTextAsync(candidate.SvgPath!, cancellationToken);
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Join(_temp.Path, "assets", "rtkelly-labs", "rtkelly-labs.svg"), cancellationToken));
        var assets = Path.Join(_temp.Path, "tools", "image-drift", "src", "image_drift", "builder_assets", "rtkelly-labs");
        Assert.Equal(expected, await File.ReadAllTextAsync(Path.Join(assets, "master.svg"), cancellationToken));
        Assert.Equal(Incumbent, await File.ReadAllTextAsync(Path.Join(variations, "rtkelly-labs", "r1", "incumbent-backup.svg"), cancellationToken));
        var manifest = await File.ReadAllTextAsync(Path.Join(assets, "manifest.json"), cancellationToken);
        Assert.Contains("\"run_id\": \"r1\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"candidate\": 1", manifest, StringComparison.Ordinal);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected))), manifest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Promote_keeps_the_other_variants_of_the_manifest_on_disk_rather_than_the_embedded_copy()
    {
        await Arrange();
        var cancellationToken = TestContext.Current.CancellationToken;
        var manifestFile = Path.Join(_temp.Path, "tools", "image-drift", "src", "image_drift", "builder_assets", "rtkelly-labs", "manifest.json");
        var onDisk = LabsBuilder.Manifest();
        var edited = onDisk with { Variants = onDisk.Variants.ToDictionary(v => v.Key, v => v.Key == "favicon" ? v.Value with { Sha256 = "feedface" } : v.Value) };
        await File.WriteAllTextAsync(manifestFile, LabsBuilder.ManifestJson(edited), cancellationToken);
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);

        var exit = await Run("image", "promote", "--run-id", "r1", "--candidate", "1", "--confirm", "--variations-dir", variations);

        Assert.Equal(0, exit);
        var written = LabsBuilder.ParseManifest(manifestFile, await File.ReadAllTextAsync(manifestFile, cancellationToken));
        Assert.Equal("feedface", written.Variants["favicon"].Sha256);
        Assert.Equal("r1", written.Variants["master"].PromotedFrom!.RunId);
    }

    [Fact]
    public async Task Promote_re_runs_the_gate_and_refuses_a_candidate_that_no_longer_wins()
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);
        _candidateGrey = 100;

        var exit = await Run("image", "promote", "--run-id", "r1", "--candidate", "1", "--confirm", "--variations-dir", variations);

        Assert.Equal(1, exit);
        Assert.Contains("Candidate does not beat the incumbent:", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(Incumbent, await File.ReadAllTextAsync(Path.Join(_temp.Path, "assets", "rtkelly-labs", "rtkelly-labs.svg"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", "1", "expected --run-id")]
    [InlineData("missing", "1", "expected a run 'missing'")]
    [InlineData("r1", "9", "expected run 'r1' to hold candidate 9")]
    public async Task Promote_bad_inputs_exit_two_naming_what_was_expected(string runId, string candidate, string message)
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);

        var exit = await Run("image", "promote", "--run-id", runId, "--candidate", candidate, "--variations-dir", variations);

        Assert.Equal(2, exit);
        Assert.Contains(message, _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explore_without_a_master_svg_exits_two()
    {
        await Arrange();
        File.Delete(Path.Join(_temp.Path, "assets", "rtkelly-labs", "rtkelly-labs.svg"));

        var exit = await Run("image", "explore");

        Assert.Equal(2, exit);
        Assert.Contains("expected the master SVG at", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explore_and_promote_name_a_missing_chromium_and_exit_two()
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);
        _launch = _ => throw new RendererUnavailableException("no browser");

        var explore = await Run("image", "explore", "--variations-dir", variations);
        var promote = await Run("image", "promote", "--run-id", "r1", "--candidate", "1", "--variations-dir", variations);

        Assert.Equal([2, 2], [explore, promote]);
        Assert.Contains("Chromium is required to score candidates (no browser)", _err.ToString(), StringComparison.Ordinal);
        Assert.Contains("Chromium is required to re-run the gate (no browser)", _err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explore_and_promote_report_an_io_failure_while_scoring_and_exit_two()
    {
        await Arrange();
        var variations = Path.Join(_temp.Path, "variations");
        await Run("image", "explore", "--run-id", "r1", "--variations-dir", variations);
        _launch = _ => throw new IOException("disk gone");

        var explore = await Run("image", "explore", "--variations-dir", variations);
        var promote = await Run("image", "promote", "--run-id", "r1", "--candidate", "1", "--variations-dir", variations);

        Assert.Equal([2, 2], [explore, promote]);
        Assert.Equal(2, _err.ToString().Split("Error: disk gone").Length - 1);
    }

    [Fact]
    public async Task Promote_rejects_a_project_that_is_not_a_labs_project()
    {
        await Arrange();

        var exit = await Run("image", "promote", "--project", "parquet", "--run-id", "r1");

        Assert.Equal(2, exit);
        Assert.Contains("Error:", _err.ToString(), StringComparison.Ordinal);
    }

    private async Task Arrange()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var project = Path.Join(_temp.Path, "assets", "rtkelly-labs");
        Directory.CreateDirectory(Path.Join(project, "reference"));
        var builderAssets = Path.Join(_temp.Path, "tools", "image-drift", "src", "image_drift", "builder_assets", "rtkelly-labs");
        Directory.CreateDirectory(builderAssets);
        await File.WriteAllTextAsync(Path.Join(builderAssets, "manifest.json"), LabsBuilder.ManifestJson(LabsBuilder.Manifest()), cancellationToken);
        await File.WriteAllTextAsync(Path.Join(project, "rtkelly-labs.svg"), Incumbent, cancellationToken);
        var white = new byte[8 * 8 * 4];
        Array.Fill(white, (byte)255);
        await File.WriteAllBytesAsync(Path.Join(project, "reference", "rtkelly-labs.png"), RasterPipeline.EncodePng(new Raster(8, 8, white)), cancellationToken);
    }

    private Task<int> Run(params string[] args)
    {
        var host = new ImageHost(
            _temp.Path,
            Path.Join(_temp.Path, "scratch"),
            _launch ?? (_ => Task.FromResult<ISvgRenderer>(new Scripted(() => _candidateGrey))),
            () => throw new InvalidOperationException("the fast renderer never decides explore or promote"));
        return new ImageCommand(host, _out, _err).Build().Parse(args).InvokeAsync(
            new InvocationConfiguration { Output = _out, Error = _err }, TestContext.Current.CancellationToken);
    }

    /// <summary>Renders the incumbent mid grey and every candidate as <c>grey()</c>, so a white reference prefers a white candidate.</summary>
    private sealed class Scripted(Func<int> grey) : ISvgRenderer
    {
        public string Name => "Scripted";

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
        {
            var value = Path.GetFileName(svgPath).StartsWith("incumbent", StringComparison.Ordinal) ? 128 : grey();
            var pixels = new byte[size * size * 4];
            Array.Fill(pixels, (byte)value);
            return Task.FromResult(new Raster(size, size, pixels));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
