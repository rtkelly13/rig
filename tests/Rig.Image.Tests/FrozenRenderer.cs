namespace Rig.Image.Tests;

/// <summary>Serves the oracle's frozen Chromium renders as an <see cref="ISvgRenderer"/>, so the cross-check runs without a browser.</summary>
internal sealed class FrozenRenderer : ISvgRenderer
{
    public string Name => "Chromium (frozen)";

    public Task<Raster> RenderAsync(string svgPath, int size, CancellationToken cancellationToken)
    {
        var key = Relative(svgPath);
        foreach (var render in OracleFixture.Document.Property("renders").Items())
        {
            if (render.Property("svg").AsString() == key && render.Property("size").AsInteger() == size)
            {
                return Task.FromResult(RasterPipeline.Decode(OracleFixture.Bytes(render.Property("file").AsString())));
            }
        }

        throw new InvalidOperationException($"expected a frozen render of {key} at {size}, found none in the oracle");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Relative(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in new[] { OracleFixture.PathOf(string.Empty), OracleFixture.RepoRoot })
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.Ordinal))
            {
                return full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        throw new InvalidOperationException($"expected a path under the repository or the oracle, found {path}");
    }
}
