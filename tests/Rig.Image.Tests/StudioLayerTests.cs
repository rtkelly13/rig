using System.Globalization;
using System.Text.Json;
using Rig.Image.Gallery;
using Rig.Image.Pathways;

namespace Rig.Image.Tests;

/// <summary>Production cuts, drift guidance, sheets, the asset catalog, the studio API and the gallery templates, against the frozen studio oracle where it has one.</summary>
public sealed class StudioLayerTests : IDisposable
{
    private const string Header = "filename,cut_type,width,height,format,purpose,description\n";

    private static readonly StrictJson Oracle = StrictJson.Parse(
        "studio-oracle.json",
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StudioOracle", "studio-oracle.json")));

    private readonly string _root = Path.Join(Path.GetTempPath(), "rig-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Production_cuts_parse_in_file_order_and_the_oracle_specs_match_the_committed_csv()
    {
        foreach (var project in Oracle.Property("cut_specs").PropertyNames())
        {
            var expected = Oracle.Property("cut_specs").Property(project).Items();
            var actual = ProductionCuts.Read(Path.Join(OracleFixture.RepoRoot, "assets", project, "production-cuts", "production-cuts.csv"));

            foreach (var item in expected)
            {
                var cut = actual.Single(candidate => candidate.Filename == item.Property("filename").AsString());
                Assert.Equal(item.Property("cut_type").AsString(), cut.CutType);
                Assert.Equal(item.Property("width").AsInteger(), cut.Width);
                Assert.Equal(item.Property("height").AsInteger(), cut.Height);
                Assert.Equal(item.Property("format").AsString(), cut.Format);
            }
        }
    }

    [Fact]
    public void Production_cuts_skip_blank_filenames_handle_quotes_and_lower_the_format()
    {
        var cuts = ProductionCuts.Parse("x.csv", Header + "a.PNG,avatar,64,64,PNG,\"one, two\",\"say \"\"hi\"\"\"\n,skipped,1,1,png,,\nb.svg,master_svg,10,10,svg\n");

        Assert.Equal(["a.PNG", "b.svg"], cuts.Select(cut => cut.Filename));
        Assert.Equal("png", cuts[0].Format);
        Assert.Equal("one, two", cuts[0].Purpose);
        Assert.Equal("say \"hi\"", cuts[0].Description);
        Assert.True(ProductionCuts.IsRaster(cuts[0]));
        Assert.False(ProductionCuts.IsRaster(cuts[1]));
    }

    [Theory]
    [InlineData("", "expected a header row")]
    [InlineData("filename,cut_type,width,height\na,b,1,1\n", "expected a 'format' column in the header")]
    [InlineData(Header + "a.png,avatar,wide,64,png,,\n", "width")]
    public void Production_cuts_name_the_file_and_the_problem(string text, string message)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ProductionCuts.Parse("x.csv", text));

        Assert.StartsWith("x.csv:", exception.Message, StringComparison.Ordinal);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_cuts_that_are_missing_name_the_path()
    {
        var exception = Assert.Throws<FileNotFoundException>(() => ProductionCuts.Read(Path.Join(_root, "absent.csv")));

        Assert.Contains("absent.csv", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_writes_svg_png_and_ico_cuts_choosing_the_favicon_variant_by_cut_type()
    {
        Directory.CreateDirectory(_root);
        var master = Path.Join(_root, "master.svg");
        var favicon = Path.Join(_root, "favicon.svg");
        await File.WriteAllTextAsync(master, "<svg id=\"master\"/>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(favicon, "<svg id=\"favicon\"/>", TestContext.Current.CancellationToken);
        await using var renderer = new RecordingRenderer();
        var cuts = ProductionCuts.Parse("x.csv", Header + "m.svg,master_svg,10,10,svg\navatar.png,avatar,16,16,png\nf.png,favicon_32,32,32,png\nfavicon.ico,favicon,48,48,ico\n");

        var written = await ProductionCuts.ExportAsync(cuts, Path.Join(_root, "out"), master, favicon, renderer, TestContext.Current.CancellationToken);

        Assert.Equal(["m.svg", "avatar.png", "f.png", "favicon.ico"], written.Select(Path.GetFileName));
        Assert.Equal([master, favicon, favicon], renderer.Sources);
        Assert.Equal([16, 32, 48], renderer.Sizes);
        Assert.Equal("<svg id=\"master\"/>", await File.ReadAllTextAsync(written[0], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Export_an_unknown_format_names_the_cut()
    {
        Directory.CreateDirectory(_root);
        var master = Path.Join(_root, "master.svg");
        await File.WriteAllTextAsync(master, "<svg/>", TestContext.Current.CancellationToken);
        var cuts = ProductionCuts.Parse("x.csv", Header + "a.webp,avatar,16,16,webp\n");

        await using var renderer = new RecordingRenderer();
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => ProductionCuts.ExportAsync(cuts, _root, master, master, renderer, TestContext.Current.CancellationToken));

        Assert.Contains("a.webp", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Drift_guidance_matches_the_oracle_cases_apart_from_naming_the_builder_without_its_old_language()
    {
        foreach (var guidance in Oracle.Property("guidance").Items())
        {
            var a = guidance.Property("args").Items();
            var hotspots = a[6].Items().Select(mode => new Hotspot(1, 10, 20, 30, 40, 3.5, mode.AsString())).ToList();
            var drift = new DriftNumbers(a[0].AsNumber(), a[1].AsNumber(), 30, [], a[2].AsNumber(), a[3].AsNumber(), 1, a[4].AsNumber(), a[5].AsNumber(), hotspots);

            var expected = guidance.Property("recommendations").Items().Select(item => item.AsString().Replace("in the .py builder", "in the builder", StringComparison.Ordinal));

            Assert.Equal(expected, DriftReport.Recommendations(drift));
        }
    }

    [Fact]
    public void Drift_hotspot_summaries_cover_each_mode_and_reject_others()
    {
        Assert.Contains("Edge", DriftReport.HotspotSummary("GEOMETRY_EDGE"), StringComparison.Ordinal);
        Assert.Contains("Color", DriftReport.HotspotSummary("CHROMATIC_COLOR"), StringComparison.Ordinal);
        Assert.Contains("Combined", DriftReport.HotspotSummary("MIXED"), StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => DriftReport.HotspotSummary("nope"));
        Assert.Equal("∞", DriftReport.Psnr(double.PositiveInfinity));
        Assert.Equal("31.5 dB", DriftReport.Psnr(31.52));
    }

    [Fact]
    public void The_heatmap_reddens_by_three_times_the_difference_and_dims_the_rest()
    {
        var reference = new RgbImage(1, 1, [10, 20, 30]);
        var render = new RgbImage(1, 1, [40, 50, 100]);

        var heat = DriftReport.Heatmap(reference, render);

        Assert.Equal([(byte)(3 * ((30 + 30 + 70) / 3f)), 15, 30], heat.Pixels.ToArray());
        Assert.Throws<ArgumentException>(() => DriftReport.Heatmap(reference, new RgbImage(2, 1, new byte[6])));
    }

    [Fact]
    public void Sheets_size_the_grid_from_the_panels_and_encode_to_png()
    {
        var panel = new SheetPanel(new RgbImage(2, 2, new byte[12]), "label", true);

        var sheet = Sheets.ContactSheet([panel, panel, panel], cell: 20, columns: 2, gap: 4, labelHeight: 10);

        Assert.Equal((2 * 20) + (3 * 4), sheet.Width);
        Assert.Equal((2 * 30) + (3 * 4), sheet.Height);
        Assert.Equal(20, Sheets.ContactSheet([], cell: 20).Width);
        var png = RasterPipeline.Decode(Sheets.EncodePng(sheet));
        Assert.Equal(sheet.Width, png.Width);
        var comparison = Sheets.ComparisonSheet(panel.Image, panel.Image, panel.Image, 1.5, 0.9, cell: 20);
        var plain = Sheets.ComparisonSheet(panel.Image, panel.Image, null, null, null, cell: 20);
        Assert.True(comparison.Width > plain.Width);
        _ = Sheets.ComparisonSheet(panel.Image, panel.Image, panel.Image, cell: 20);
    }

    [Fact]
    public void The_catalog_of_the_committed_assets_matches_the_oracle()
    {
        var catalog = new AssetCatalog(Path.Join(OracleFixture.RepoRoot, "assets"), Path.Join(_root, "variations"));

        foreach (var name in Oracle.Property("listed").Items().Select(item => item.AsString()))
        {
            Assert.Contains(name, catalog.Projects());
        }

        foreach (var name in Oracle.Property("projects").PropertyNames())
        {
            var expected = Oracle.Property("projects").Property(name);
            var actual = catalog.Index(name);

            Assert.Equal(expected.Property("master_svg").AsString(), actual.MasterSvg);
            Assert.Equal(expected.Property("master_png").AsString(), actual.MasterPng);
            Assert.Equal(
                expected.Property("references").Items().Select(item => (item.Items()[0].AsInteger(), item.Items()[1].AsString(), item.Items()[2].AsString())),
                actual.References.Where(reference => reference.SequenceNumber < 90).Select(reference => (reference.SequenceNumber, reference.Filename, reference.Description)));
            foreach (var item in expected.Property("cuts").Items())
            {
                var cut = actual.Cuts.Single(candidate => candidate.Filename == item.Items()[1].AsString());
                Assert.Equal(item.Items()[0].AsString(), cut.CutType);
                Assert.Equal(item.Items()[3].AsInteger(), cut.Width);
                Assert.Equal(item.Items()[5].AsString(), cut.Format);
            }
        }
    }

    [Fact]
    public void A_project_without_a_csv_has_no_cuts_and_service_folders_are_not_projects()
    {
        Directory.CreateDirectory(Path.Join(_root, "assets", "demo", "production-cuts"));
        File.WriteAllText(Path.Join(_root, "assets", "demo", "production-cuts", "ignored.png"), "x");
        foreach (var hidden in new[] { ".git", "_x", "testing", "temp" })
        {
            Directory.CreateDirectory(Path.Join(_root, "assets", hidden));
        }

        var catalog = new AssetCatalog(Path.Join(_root, "assets"), Path.Join(_root, "variations"));

        Assert.Equal(["demo"], catalog.Projects());
        Assert.Empty(catalog.Index("demo").Cuts);
        Assert.Null(catalog.Index("demo").MasterSvg);
        Assert.Empty(new AssetCatalog(Path.Join(_root, "absent"), _root).Projects());
    }

    [Fact]
    public void The_studio_lists_projects_with_media_urls_and_diffs_a_candidate_against_its_reference()
    {
        var assets = Path.Join(_root, "assets");
        var folder = Path.Join(assets, "demo", "reference");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Join(folder, "01-ref.png"), RasterPipeline.EncodePng(Solid(8, 40)));
        File.WriteAllBytes(Path.Join(folder, "02-cand.png"), RasterPipeline.EncodePng(Solid(8, 90)));
        var api = new StudioApi(assets, Path.Join(_root, "variations"));

        var projects = JsonDocument.Parse(api.Projects().Body);
        var demo = projects.RootElement.GetProperty("projects")[0];
        Assert.Equal("demo", demo.GetProperty("name").GetString());
        Assert.Equal("/media/assets/demo/reference/01-ref.png", demo.GetProperty("references")[0].GetProperty("url").GetString());

        var diff = StudioApi.Diff(Path.Join(folder, "01-ref.png"), Path.Join(folder, "02-cand.png"), path => path);
        Assert.Equal(200, diff.Status);
        Assert.True(JsonDocument.Parse(diff.Body).RootElement.GetProperty("rmse_pct").GetDouble() > 0);
    }

    [Fact]
    public void The_studio_diff_reports_missing_and_unresolvable_inputs_and_unreadable_images()
    {
        Assert.Equal(400, StudioApi.Diff(null, "x", path => path).Status);
        Assert.Equal(404, StudioApi.Diff("a", "b", _ => null).Status);

        Directory.CreateDirectory(_root);
        var bad = Path.Join(_root, "bad.png");
        File.WriteAllText(bad, "not a png");
        Assert.True(StudioApi.Diff(bad, bad, path => path).Status >= 400);
    }

    [Fact]
    public void Snapshot_requests_are_validated_and_recorded()
    {
        var api = new StudioApi(Path.Join(_root, "assets"), Path.Join(_root, "variations"));

        Assert.Equal(400, api.Snapshot("{broken").Status);
        Assert.Equal(400, api.Snapshot("[1]").Status);
        Assert.Equal(400, api.Snapshot("{\"project\": 3}").Status);
        Assert.Equal(400, api.Snapshot("{\"project\": \"p\", \"run_id\": \"r\", \"step_idx\": \"x\"}").Status);
        Assert.Equal(400, api.Snapshot("{\"project\": \"p\"}").Status);

        var ok = api.Snapshot("{\"project\": \"p\", \"run_id\": \"r\", \"step_idx\": 2, \"step_name\": \"n\", \"svg_content\": \"<svg/>\", \"metrics\": {\"ssim\": 0.5}, \"notes\": \"why\"}");

        Assert.Equal(200, ok.Status);
        Assert.Single(Directory.GetFiles(Path.Join(_root, "variations", "p", "r"), "*.svg"));
        Assert.Equal(200, api.Snapshot("{\"project\": \"p\", \"run_id\": \"r2\"}").Status);
    }

    [Fact]
    public void The_gallery_badge_class_steps_at_twenty_and_twenty_eight()
    {
        Assert.Equal("badge-good", GalleryBuilder.BadgeClass(19.9));
        Assert.Equal("badge-warn", GalleryBuilder.BadgeClass(20));
        Assert.Equal("badge-warn", GalleryBuilder.BadgeClass(27.9));
        Assert.Equal("badge-bad", GalleryBuilder.BadgeClass(28));
    }

    [Fact]
    public void The_gallery_template_escapes_text_keeps_html_fragments_and_names_a_missing_value()
    {
        var values = new Dictionary<string, string> { ["title"] = "<b>", ["body_html"] = "<i>x</i>" };

        Assert.Equal("&lt;b&gt; <i>x</i>", GalleryTemplates.Fill("t.html", "{{title}} {{body_html}}", values));
        var exception = Assert.Throws<InvalidOperationException>(() => GalleryTemplates.Fill("t.html", "{{missing}}", values));
        Assert.Contains("expected a value for {{missing}} in t.html", exception.Message, StringComparison.Ordinal);
        Assert.Contains("body_html, title", exception.Message, StringComparison.Ordinal);
        Assert.NotEmpty(GalleryTemplates.Css);
    }

    [Fact]
    public async Task The_gallery_choose_wave_and_single_pages_render_every_candidate_without_writing_the_master()
    {
        var reference = Path.Join(_root, "ref.png");
        var target = Path.Join(_root, "master.svg");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(reference, RasterPipeline.EncodePng(Solid(16, 120)), TestContext.Current.CancellationToken);
        var variants = LabsBuilder.Variants;
        var input = new GalleryInput(
            "demo",
            reference,
            target,
            [new GalleryRegion("crossover_core", "Crossover Core", FeatureRegions.For("rtkelly-labs").First(box => box.Key == "crossover_core"))],
            variants,
            LabsBuilder.DefaultVariant,
            key => LabsBuilder.GenerateSvg(key));
        await using var renderer = new RecordingRenderer();

        var choose = await GalleryBuilder.ChooseAsync(input, renderer, Path.Join(_root, "scratch"), TestContext.Current.CancellationToken);
        var wave = await GalleryBuilder.WaveAsync(input, renderer, Path.Join(_root, "scratch"), TestContext.Current.CancellationToken);
        var single = await GalleryBuilder.SingleAsync(input, renderer, Path.Join(_root, "scratch"), TestContext.Current.CancellationToken);

        foreach (var run in new[] { choose, wave, single })
        {
            Assert.Contains("<html", run.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", run.Html, StringComparison.Ordinal);
            Assert.NotEmpty(run.Lines);
        }

        Assert.False(File.Exists(target));
    }

    private static Raster Solid(int size, byte value)
    {
        var pixels = new byte[size * size * 4];
        Array.Fill(pixels, value);
        return new Raster(size, size, pixels);
    }

    private sealed class RecordingRenderer : ISvgRenderer
    {
        public List<string> Sources { get; } = [];

        public List<int> Sizes { get; } = [];

        public string Name => "Recording";

        public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
        {
            Sources.Add(svgPath);
            Sizes.Add(size);
            return Task.FromResult(Solid(size, 100));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
