using System.Globalization;

namespace Rig.Image;

/// <summary>Whether a candidate may replace the incumbent master, and why not.</summary>
/// <param name="Promotable">True when every gate passed.</param>
/// <param name="Reasons">One line per failed gate, worded like the original.</param>
/// <param name="CompositeDelta">Incumbent composite minus candidate composite; positive means better.</param>
/// <param name="SsimDelta">Candidate SSIM minus incumbent SSIM; positive means better.</param>
/// <param name="WorstFeatureDelta">The largest per-region regression; positive means a region got worse.</param>
/// <param name="WorstFeatureKey">The region that regressed most, or null.</param>
public sealed record PromotionVerdict(
    bool Promotable,
    IReadOnlyList<string> Reasons,
    double CompositeDelta,
    double SsimDelta,
    double WorstFeatureDelta,
    string? WorstFeatureKey);

/// <summary>the original promotion gate, over scores already computed in one scoring pass.</summary>
public static class PromotionGate
{
    /// <summary>A composite must beat the incumbent by more than this many percentage points; less is a tie.</summary>
    public const double TieThresholdPct = 0.5;

    /// <summary>No feature region may regress by more than this many percentage points.</summary>
    public const double MaxFeatureRegressionPct = 0.25;

    /// <summary>Decides whether <paramref name="candidate"/> may replace <paramref name="incumbent"/>.</summary>
    public static PromotionVerdict Evaluate(Score incumbent, double incumbentSsim, Score candidate, double candidateSsim)
    {
        ArgumentNullException.ThrowIfNull(incumbent);
        ArgumentNullException.ThrowIfNull(candidate);
        var reasons = new List<string>();
        var compositeDelta = incumbent.CompositePct - candidate.CompositePct;
        if (compositeDelta < TieThresholdPct)
        {
            reasons.Add(
                $"composite {DriftMetrics.Fixed(candidate.CompositePct, 3)}% vs incumbent {DriftMetrics.Fixed(incumbent.CompositePct, 3)}% "
                + $"(needs to beat it by >{TieThresholdPct.ToString(CultureInfo.InvariantCulture)}pp, got {DriftMetrics.Signed(compositeDelta, 3)}pp)");
        }

        var ssimDelta = candidateSsim - incumbentSsim;
        if (ssimDelta < 0)
        {
            reasons.Add(
                $"SSIM regressed: {DriftMetrics.Fixed(candidateSsim, 4)} vs incumbent {DriftMetrics.Fixed(incumbentSsim, 4)} ({DriftMetrics.Signed(ssimDelta, 4)})");
        }

        string? worstKey = null;
        var worstDelta = 0.0;
        foreach (var (key, incumbentPct) in incumbent.PerFeaturePct)
        {
            if (!candidate.PerFeaturePct.TryGetValue(key, out var candidatePct))
            {
                reasons.Add($"candidate is missing feature region '{key}'");
                continue;
            }

            var delta = candidatePct - incumbentPct;
            if (delta > worstDelta)
            {
                worstDelta = delta;
                worstKey = key;
            }
        }

        if (worstDelta > MaxFeatureRegressionPct)
        {
            reasons.Add(
                $"feature '{worstKey}' regressed by {DriftMetrics.Signed(worstDelta, 3)}pp (limit {MaxFeatureRegressionPct.ToString(CultureInfo.InvariantCulture)}pp)");
        }

        return new PromotionVerdict(reasons.Count == 0, reasons, compositeDelta, ssimDelta, worstDelta, worstKey);
    }
}
