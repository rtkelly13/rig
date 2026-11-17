using System.Text.Json;

namespace ImageStackEval;

public sealed record RefHotspot(int Rank, int X0, int Y0, int X1, int Y1, double ErrorPct, string DominantMode);

public sealed record RefDrift(
    double RmsePct,
    double Ssim,
    double PsnrDb,
    double LuminanceRmsePct,
    double ChrominanceRmsePct,
    double ColorToGeometryRatio,
    double EdgeCorrelation,
    double BoundaryDilationBias,
    List<RefHotspot> Hotspots);

public sealed record RefScore(double CompositePct, double FullFramePct, Dictionary<string, double> PerFeaturePct);

public sealed record RefRender(string Id, string Svg, int Size, double Seconds);

public sealed record RefMetric(string Id, string Project, string Reference, string RenderId, RefDrift Drift, RefScore Score);

public sealed record RefPromotion(
    string Project,
    string Reference,
    string IncumbentRenderId,
    string Candidate,
    string CandidateRenderId,
    RefScore IncumbentScore,
    double IncumbentSsim,
    RefScore CandidateScore,
    double CandidateSsim,
    bool Promotable,
    double CompositeDelta,
    double SsimDelta,
    double WorstFeatureDelta,
    string? WorstFeatureKey,
    List<string> Reasons);

public sealed record RefRegion(string Key, int X0, int Y0, int X1, int Y1);

/// <summary>What reference_dump.py wrote: every number the C# side is compared with.</summary>
public sealed record ReferenceDocument(
    string Chromium,
    double ChromiumLaunchSeconds,
    Dictionary<string, List<RefRegion>> Regions,
    List<RefRender> Renders,
    List<RefMetric> Metrics,
    List<RefPromotion> Promotion)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static ReferenceDocument Load(string path) =>
        JsonSerializer.Deserialize<ReferenceDocument>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException("reference.json is empty");
}
