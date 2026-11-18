namespace Rig.Image.Tests;

/// <summary>The frozen Python pathways oracle (dumped from the original implementation before it was retired) and the repository files it was taken from.</summary>
internal static class PathwaysOracleFixture
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "PathwaysOracle");

    /// <summary>The parsed pathways-oracle.json.</summary>
    public static StrictJson Document { get; } = StrictJson.Parse("pathways-oracle.json", File.ReadAllText(Path.Combine(Folder, "pathways-oracle.json")));

    /// <summary>A frozen SVG written beside the oracle.</summary>
    public static string Svg(string name) => File.ReadAllText(Path.Combine(Folder, "svg", name));

    /// <summary>A repository file, found by walking up to rig.slnx.</summary>
    public static string RepoText(string relative) => File.ReadAllText(Path.Combine(OracleFixture.RepoRoot, relative));
}
