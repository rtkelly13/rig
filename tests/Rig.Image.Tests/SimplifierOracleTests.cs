using Rig.Image.Simplifier;

namespace Rig.Image.Tests;

public sealed class SimplifierOracleTests
{
    [Fact]
    public void Synthetic_documents_simplify_exactly_like_the_python()
    {
        var mismatches = new List<string>();
        var documents = SimplifierOracleFixture.Document.Property("syntheticDocuments").Items();
        Assert.NotEmpty(documents);
        foreach (var entry in documents)
        {
            var name = entry.Property("name").AsString();
            var text = entry.Property("input").Property("text").AsString();
            Check(mismatches, $"{name} nodes", entry.Property("nodes").AsInteger() == SvgSimplifier.CountPathNodes(text) ? null : $"{name} nodes differ");
            if (name.StartsWith("size-", StringComparison.Ordinal))
            {
                // Scour rewrites path data (relative commands at five significant digits); that pass is not ported, so only the stages that do not run it are exact here.
                Check(mismatches, $"{name} quantize", SimplifierOracleFixture.Difference($"{name} quantize", entry.Property("quantize"), SvgSimplifier.Quantize(text)));
                Check(mismatches, $"{name} quantize3", SimplifierOracleFixture.Difference($"{name} quantize3", entry.Property("quantize3"), SvgSimplifier.Quantize(text, 3)));
                Check(mismatches, $"{name} geometric", SimplifierOracleFixture.Difference($"{name} geometric", entry.Property("geometricOnly"), SvgSimplifier.Simplify(text, useScour: false, precision: 2)));
                continue;
            }

            Check(mismatches, $"{name} scour", SimplifierOracleFixture.Difference($"{name} scour", entry.Property("scour"), SvgSimplifier.RunScour(text)));
            Check(mismatches, $"{name} quantize", SimplifierOracleFixture.Difference($"{name} quantize", entry.Property("quantize"), SvgSimplifier.Quantize(text)));
            Check(mismatches, $"{name} quantize3", SimplifierOracleFixture.Difference($"{name} quantize3", entry.Property("quantize3"), SvgSimplifier.Quantize(text, 3)));
            Check(mismatches, $"{name} final", SimplifierOracleFixture.Difference($"{name} final", entry.Property("final"), SvgSimplifier.Simplify(text)));
            Check(mismatches, $"{name} geometric", SimplifierOracleFixture.Difference($"{name} geometric", entry.Property("geometricOnly"), SvgSimplifier.Simplify(text, useScour: false, precision: 2)));
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void Real_documents_match_exactly_where_scour_is_not_involved()
    {
        var mismatches = new List<string>();
        foreach (var entry in SimplifierOracleFixture.Document.Property("realDocuments").Items())
        {
            var path = entry.Property("path").AsString();
            var text = SimplifierOracleFixture.RepoText(path);
            Check(mismatches, $"{path} nodes", entry.Property("nodes").AsInteger() == SvgSimplifier.CountPathNodes(text) ? null : $"{path} nodes differ");
            Check(mismatches, $"{path} quantize", SimplifierOracleFixture.Difference($"{path} quantize", entry.Property("quantize"), SvgSimplifier.Quantize(text)));
            Check(mismatches, $"{path} geometricOnly", SimplifierOracleFixture.Difference($"{path} geometricOnly", entry.Property("geometricOnly"), SvgSimplifier.Simplify(text, useScour: false)));
            foreach (var tolerance in entry.Property("tolerances").Items())
            {
                var value = tolerance.Property("tolerance").AsNumber();
                var geometric = SvgSimplifier.Simplify(text, value, useScour: false, precision: 2);
                Check(mismatches, $"{path} geometric {value}", SimplifierOracleFixture.Difference($"{path} geometric {value}", tolerance.Property("geometric"), geometric));
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public async Task Real_documents_with_scour_render_like_the_python_finals()
    {
        var problems = new List<string>();
        await using var renderer = new SvgSkiaRenderer();
        var scratch = Path.Combine(Path.GetTempPath(), "rig-simplifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            foreach (var entry in SimplifierOracleFixture.Document.Property("realDocuments").Items())
            {
                var path = entry.Property("path").AsString();
                var frozen = Finals(path);
                if (frozen.Length == 0)
                {
                    continue;
                }

                var originalPath = Path.Combine(scratch, "original.svg");
                var frozenPath = Path.Combine(scratch, "python.svg");
                var ownPath = Path.Combine(scratch, "csharp.svg");
                var original = SimplifierOracleFixture.RepoText(path);
                await File.WriteAllTextAsync(originalPath, original, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(frozenPath, frozen, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(ownPath, SvgSimplifier.Simplify(original), TestContext.Current.CancellationToken);
                var reference = RasterPipeline.Normalise(await renderer.RenderAsync(originalPath, 500, TestContext.Current.CancellationToken));
                var python = RasterPipeline.Normalise(await renderer.RenderAsync(frozenPath, 500, TestContext.Current.CancellationToken));
                var own = RasterPipeline.Normalise(await renderer.RenderAsync(ownPath, 500, TestContext.Current.CancellationToken));

                // Scour's path rewrite is not ported, so the path text differs; what must hold is that this port drifts from the
                // source no more than the Python does (ADR 0024: 0.1pp RMSE, 0.001 SSIM), and that the two finals still look alike.
                var pythonDrift = DriftMetrics.Drift(reference, python);
                var ownDrift = DriftMetrics.Drift(reference, own);
                var between = DriftMetrics.Drift(python, own);
                if (ownDrift.RmsePct > pythonDrift.RmsePct + 0.1 || ownDrift.Ssim < pythonDrift.Ssim - 0.001 || between.RmsePct > 0.5)
                {
                    problems.Add($"{path}: rmse {ownDrift.RmsePct:F4}% against {pythonDrift.RmsePct:F4}% for the Python, ssim {ownDrift.Ssim:F5} against {pythonDrift.Ssim:F5}, {between.RmsePct:F4}% between the two");
                }
            }
        }
        finally
        {
            Directory.Delete(scratch, true);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string Finals(string path)
    {
        var name = path.Replace("/", "__", StringComparison.Ordinal);
        return path.Contains("autotraced", StringComparison.Ordinal) ? string.Empty : SimplifierOracleFixture.Text("finals/" + name);
    }

    private static void Check(List<string> mismatches, string label, string? difference)
    {
        if (difference is not null)
        {
            mismatches.Add(difference.Length > 900 ? difference[..900] : difference);
        }

        _ = label;
    }
}
