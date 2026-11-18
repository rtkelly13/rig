namespace Rig.Image;

/// <summary>A named crop box on the 500 px canvas, in PIL order: left and top inclusive, right and bottom exclusive.</summary>
/// <param name="Key">The region's name, such as <c>crossover_core</c>.</param>
/// <param name="X0">Left edge.</param>
/// <param name="Y0">Top edge.</param>
/// <param name="X1">Right edge.</param>
/// <param name="Y1">Bottom edge.</param>
public sealed record FeatureBox(string Key, int X0, int Y0, int X1, int Y1);

/// <summary>The feature regions each project's candidates are scored on, frozen from the original builders.</summary>
public static class FeatureRegions
{
    private const string Resource = "Rig.Image.feature-regions.json";

    /// <summary>The regions of one project, from the embedded <c>feature-regions.json</c>.</summary>
    public static IReadOnlyList<FeatureBox> For(string project)
    {
        using var stream = typeof(FeatureRegions).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"{Resource} is not embedded in {typeof(FeatureRegions).Assembly.GetName().Name}");
        using var reader = new StreamReader(stream);
        var projects = StrictJson.Parse(Resource, reader.ReadToEnd());
        return Read(projects.Property(project));
    }

    /// <summary>Reads an array of <c>{key, x0, y0, x1, y1}</c> objects strictly.</summary>
    public static IReadOnlyList<FeatureBox> Read(StrictJson array)
    {
        ArgumentNullException.ThrowIfNull(array);
        return [.. array.Items().Select(item => new FeatureBox(
            item.Property("key").AsString(),
            item.Property("x0").AsInteger(),
            item.Property("y0").AsInteger(),
            item.Property("x1").AsInteger(),
            item.Property("y1").AsInteger()))];
    }
}
