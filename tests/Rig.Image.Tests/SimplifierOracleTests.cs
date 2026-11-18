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
            Check(mismatches, $"{name} scour", SimplifierOracleFixture.Difference($"{name} scour", entry.Property("scour"), SvgSimplifier.RunScour(text)));
            Check(mismatches, $"{name} quantize", SimplifierOracleFixture.Difference($"{name} quantize", entry.Property("quantize"), SvgSimplifier.Quantize(text)));
            Check(mismatches, $"{name} quantize3", SimplifierOracleFixture.Difference($"{name} quantize3", entry.Property("quantize3"), SvgSimplifier.Quantize(text, 3)));
            Check(mismatches, $"{name} final", SimplifierOracleFixture.Difference($"{name} final", entry.Property("final"), SvgSimplifier.Simplify(text)));
            Check(mismatches, $"{name} geometric", SimplifierOracleFixture.Difference($"{name} geometric", entry.Property("geometricOnly"), SvgSimplifier.Simplify(text, useScour: false, precision: 2)));
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    [Fact]
    public void Real_documents_simplify_exactly_like_the_python()
    {
        var mismatches = new List<string>();
        foreach (var entry in SimplifierOracleFixture.Document.Property("realDocuments").Items())
        {
            var path = entry.Property("path").AsString();
            var text = SimplifierOracleFixture.RepoText(path);
            Check(mismatches, $"{path} nodes", entry.Property("nodes").AsInteger() == SvgSimplifier.CountPathNodes(text) ? null : $"{path} nodes differ");
            Check(mismatches, $"{path} scour", SimplifierOracleFixture.Difference($"{path} scour", entry.Property("scour"), SvgSimplifier.RunScour(text)));
            Check(mismatches, $"{path} quantize", SimplifierOracleFixture.Difference($"{path} quantize", entry.Property("quantize"), SvgSimplifier.Quantize(text)));
            Check(mismatches, $"{path} geometricOnly", SimplifierOracleFixture.Difference($"{path} geometricOnly", entry.Property("geometricOnly"), SvgSimplifier.Simplify(text, useScour: false)));
            Check(mismatches, $"{path} scourOnly", SimplifierOracleFixture.Difference($"{path} scourOnly", entry.Property("scourOnly"), SvgSimplifier.Simplify(text, useGeometric: false)));
            Check(mismatches, $"{path} noGeometricPass", SimplifierOracleFixture.Difference($"{path} noGeometricPass", entry.Property("noGeometricPass"), SvgSimplifier.Simplify(text, 0.0)));
            Check(mismatches, $"{path} precision3", SimplifierOracleFixture.Difference($"{path} precision3", entry.Property("precision3"), SvgSimplifier.Simplify(text, precision: 3)));
            foreach (var tolerance in entry.Property("tolerances").Items())
            {
                var value = tolerance.Property("tolerance").AsNumber();
                var geometric = SvgSimplifier.Simplify(text, value, useScour: false, precision: 2);
                Check(mismatches, $"{path} geometric {value}", SimplifierOracleFixture.Difference($"{path} geometric {value}", tolerance.Property("geometric"), geometric));
                var final = SvgSimplifier.Simplify(text, value);
                Check(mismatches, $"{path} final {value}", SimplifierOracleFixture.Difference($"{path} final {value}", tolerance.Property("final"), final));
                Check(mismatches, $"{path} finalNodes {value}", tolerance.Property("finalNodes").AsInteger() == SvgSimplifier.CountPathNodes(final) ? null : $"{path} finalNodes {value} differ");
            }
        }

        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
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
