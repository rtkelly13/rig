using System.Runtime.InteropServices;
namespace Rig.Image.Tests;

/// <summary>The frozen Python score oracle (tools/image-drift/oracle/dump_oracle.py) and the repository assets it was taken from.</summary>
internal static class OracleFixture
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Oracle");

    /// <summary>True on the OS and architecture the oracle was frozen on, where renders are expected to match it exactly.</summary>
    public static bool FrozenOnThisPlatform()
    {
        var provenance = Document.Property("provenance");
        var os = provenance.Property("os").AsString();
        var arch = provenance.Property("arch").AsString();
        var osMatches = os switch
        {
            "Darwin" => OperatingSystem.IsMacOS(),
            "Linux" => OperatingSystem.IsLinux(),
            "Windows" => OperatingSystem.IsWindows(),
            _ => false,
        };
        var archMatches = arch switch
        {
            "arm64" => RuntimeInformation.OSArchitecture == Architecture.Arm64,
            "x86_64" or "AMD64" => RuntimeInformation.OSArchitecture == Architecture.X64,
            _ => false,
        };
        return osMatches && archMatches;
    }

    /// <summary>
    /// The ADR thresholds were measured on the frozen platform. Skia's native build rasterises small favicons differently on other
    /// platforms (CI's Linux run measured mean abs 1.43 and 43.8% differing pixels on a 16px favicon), so elsewhere the tests only
    /// bound the divergence loosely and the report-only image-cross-check job records the real numbers.
    /// </summary>
    public static DivergenceThresholds ThresholdsForThisPlatform() =>
        FrozenOnThisPlatform() ? DivergenceThresholds.Adr : DivergenceThresholds.Adr with { MeanAbsDifference = 3.0, DifferingPixelFraction = 0.60 };

    /// <summary>The repository root, found by walking up to rig.slnx.</summary>
    public static string RepoRoot { get; } = FindRoot();

    /// <summary>The parsed score-oracle.json.</summary>
    public static StrictJson Document { get; } = Read();

    /// <summary>Reads a file under the Oracle folder.</summary>
    public static byte[] Bytes(string relative) => File.ReadAllBytes(Path.Combine(Folder, relative));

    /// <summary>Reads a repository file such as a master SVG or a reference image.</summary>
    public static byte[] RepoBytes(string relative) => File.ReadAllBytes(Path.Combine(RepoRoot, relative));

    /// <summary>The full path of a file under the Oracle folder.</summary>
    public static string PathOf(string relative) => Path.Combine(Folder, relative);

    /// <summary>The oracle's render entry with this id.</summary>
    public static StrictJson Render(string id) => Document.Property("renders").Items().Single(item => item.Property("id").AsString() == id);

    /// <summary>The oracle render with this id as pixels; throws for hash-only renders.</summary>
    public static Raster RenderPixels(string id)
    {
        var file = Render(id).Property("file").AsString();
        return RasterPipeline.Decode(Bytes(file));
    }

    private static StrictJson Read()
    {
        var path = Path.Combine(Folder, "score-oracle.json");
        return StrictJson.Parse(path, File.ReadAllText(path));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "rig.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("expected rig.slnx above the test output, found none");
    }
}
