using Rig.Image.Pathways;

namespace Rig.Image.Tests;

public sealed class SnapshotManagerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "rig-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Capture_writes_the_svg_png_and_sidecar_under_the_pattern_the_indexer_reads()
    {
        var manager = new SnapshotManager(_root);

        var step = manager.CaptureStep("labs", "run-1", 3, "candidate-3", "<svg/>\r\n", [1, 2, 3], new Dictionary<string, object> { ["ssim"] = 0.5, ["promotable"] = true }, "a note", "20260101_120000");

        Assert.Equal(Path.Join(_root, "labs", "run-1", "labs__run-1__03_candidate-3__20260101_120000.svg"), step.SvgPath);
        Assert.Equal("<svg/>\n", File.ReadAllText(step.SvgPath!));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(step.PngPath!));
        Assert.Equal(
            "{\n  \"project\": \"labs\",\n  \"run_id\": \"run-1\",\n  \"step_idx\": 3,\n  \"step_name\": \"candidate-3\",\n  \"timestamp\": \"20260101_120000\",\n  \"metrics\": {\n    \"ssim\": 0.5,\n    \"promotable\": true\n  },\n  \"notes\": \"a note\"\n}",
            File.ReadAllText(step.MetadataPath!).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void GetRun_parses_steps_in_index_order_with_their_metrics_and_notes()
    {
        var manager = new SnapshotManager(_root);
        manager.CaptureStep("labs", "run-1", 2, "b", "<svg id='b'/>", null, new Dictionary<string, object> { ["x"] = 2 }, "second", "20260101_000002");
        manager.CaptureStep("labs", "run-1", 0, "incumbent", "<svg id='a'/>", null, null, "first", "20260101_000001");
        File.WriteAllText(Path.Join(_root, "labs", "run-1", "stray.txt"), "ignored");

        var run = manager.GetRun("labs", "run-1");

        Assert.NotNull(run);
        Assert.Equal([0, 2], run.Steps.Select(s => s.StepIdx));
        Assert.Equal("first", run.Steps[0].Notes);
        Assert.Equal(2, ((System.Text.Json.JsonElement)run.Steps[1].Metrics["x"]).GetInt32());
        Assert.Equal("<svg id='b'/>", File.ReadAllText(run.Steps[1].SvgPath!));
    }

    [Fact]
    public void ListRuns_is_sorted_and_skips_dot_folders_and_missing_projects()
    {
        var manager = new SnapshotManager(_root);
        manager.CaptureStep("labs", "b-run", 0, "s", "<svg/>", null, null, string.Empty, "20260101_000000");
        manager.CaptureStep("labs", "a-run", 0, "s", "<svg/>", null, null, string.Empty, "20260101_000000");
        Directory.CreateDirectory(Path.Join(_root, "labs", ".hidden"));

        Assert.Equal(["a-run", "b-run"], manager.ListRuns("labs"));
        Assert.Empty(manager.ListRuns("nobody"));
        Assert.Null(manager.GetRun("labs", "missing"));
    }

    [Fact]
    public void A_lossless_psnr_is_written_as_a_named_literal_instead_of_failing_the_capture()
    {
        var step = new SnapshotManager(_root).CaptureStep("labs", "r", 1, "s", null, null, new Dictionary<string, object> { ["psnr"] = double.PositiveInfinity }, string.Empty, "20260101_000000");

        Assert.Contains("\"psnr\": \"Infinity\"", File.ReadAllText(step.MetadataPath!), StringComparison.Ordinal);
    }

    [Fact]
    public void Prune_removes_only_the_named_run()
    {
        var manager = new SnapshotManager(_root);
        manager.CaptureStep("labs", "keep", 0, "s", "<svg/>", null, null, string.Empty, "20260101_000000");
        manager.CaptureStep("labs", "drop", 0, "s", "<svg/>", null, null, string.Empty, "20260101_000000");

        Assert.True(manager.PruneRun("labs", "drop"));
        Assert.False(manager.PruneRun("labs", "drop"));
        Assert.Equal(["keep"], manager.ListRuns("labs"));
    }

    [Fact]
    public void The_manifest_writer_reproduces_the_committed_manifest_byte_for_byte()
    {
        var committed = File.ReadAllText(Path.Join(OracleFixture.RepoRoot, "tools", "image-drift", "src", "image_drift", "builder_assets", "rtkelly-labs", "manifest.json"));

        Assert.Equal(committed.ReplaceLineEndings("\n"), LabsBuilder.ManifestJson(LabsBuilder.Manifest()));
    }

    [Fact]
    public void The_manifest_writer_records_a_promotion()
    {
        var manifest = LabsBuilder.Manifest();
        var master = manifest.Variants["master"] with { PromotedFrom = new PromotionRecord("run-9", 4, "note \"quoted\"") };

        var json = LabsBuilder.ManifestJson(manifest with { Variants = new Dictionary<string, VariantEntry>(manifest.Variants) { ["master"] = master } });

        Assert.Contains("\"promoted_from\": {\n        \"run_id\": \"run-9\",\n        \"candidate\": 4,\n        \"notes\": \"note \\\"quoted\\\"\"\n      }", json, StringComparison.Ordinal);
    }
}
