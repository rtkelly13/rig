using System.Globalization;
using Rig.Image.Simplifier;

namespace Rig.Image.Tests;

public sealed class PathOracleTests
{
    [Fact]
    public void Numbers_are_written_the_way_python_writes_them()
    {
        var mismatches = new List<string>();
        foreach (var entry in SimplifierOracleFixture.Document.Property("numbers").Items())
        {
            var value = entry.Property("value").AsNumber();
            var precision = entry.Property("precision").AsInteger();
            var actual = new PathCommand('H', [value]).Serialize(precision);
            if (actual != entry.Property("text").AsString())
            {
                mismatches.Add($"{value.ToString("R", CultureInfo.InvariantCulture)} at {precision}: expected '{entry.Property("text").AsString()}' found '{actual}'");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Path_data_parses_and_splits_like_the_python_tokenizer()
    {
        var mismatches = new List<string>();
        foreach (var entry in SimplifierOracleFixture.Document.Property("paths").Items())
        {
            var d = entry.Property("d").AsString();
            var commands = PathData.Parse(d);
            var frozen = entry.Property("commands").Items();
            if (commands.Count != frozen.Count)
            {
                mismatches.Add($"'{d}': expected {frozen.Count} commands but found {commands.Count}");
                continue;
            }

            for (var i = 0; i < frozen.Count; i++)
            {
                var item = frozen[i].Items();
                var args = item[1].Items().Select(a => a.AsNumber()).ToArray();
                if (commands[i].Command.ToString() != item[0].AsString() || !commands[i].Args.SequenceEqual(args))
                {
                    mismatches.Add($"'{d}' command {i}: expected {item[0].AsString()} [{string.Join(' ', args)}] found {commands[i].Command} [{string.Join(' ', commands[i].Args)}]");
                }
            }

            foreach (var precision in new[] { 0, 2, 3 })
            {
                var expected = entry.Property("serialized").Property(precision.ToString(CultureInfo.InvariantCulture)).AsString();
                var actual = PathData.Serialize(commands, precision);
                if (actual != expected)
                {
                    mismatches.Add($"'{d}' at {precision}: expected '{expected}' found '{actual}'");
                }
            }

            var subpaths = PathData.ExtractSubpaths(d);
            var frozenSubpaths = entry.Property("subpaths").Items();
            var same = subpaths.Count == frozenSubpaths.Count
                && subpaths.Zip(frozenSubpaths).All(pair => pair.First.IsClosed == pair.Second.Property("closed").AsBoolean() && pair.First.Commands.Count == pair.Second.Property("commands").AsInteger());
            if (!same)
            {
                mismatches.Add($"'{d}': subpaths differ ({subpaths.Count} found, {frozenSubpaths.Count} expected)");
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void Geometry_is_simplified_like_shapely()
    {
        var mismatches = new List<string>();
        foreach (var entry in SimplifierOracleFixture.Document.Property("geometry").Items())
        {
            var d = entry.Property("d").AsString();
            var results = entry.Property("results");
            foreach (var key in results.PropertyNames())
            {
                var expected = results.Property(key).AsString();
                var actual = GeometricSimplifier.SimplifyPath(d, double.Parse(key, CultureInfo.InvariantCulture));
                if (actual != expected)
                {
                    mismatches.Add($"{entry.Property("name").AsString()} at {key}: expected '{expected}' found '{actual}'");
                }
            }
        }

        Assert.Empty(mismatches);
    }
}
