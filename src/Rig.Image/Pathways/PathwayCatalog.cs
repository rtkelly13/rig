using System.Collections.Frozen;

namespace Rig.Image.Pathways;

/// <summary>What a pathway needs to build a candidate.</summary>
/// <param name="MasterSvg">The current governed master, for pathways that start from it.</param>
/// <param name="ReferencePath">The approved reference raster.</param>
public sealed record PathwayContext(string MasterSvg, string ReferencePath)
{
    /// <summary>Canonical edge axes.</summary>
    public IReadOnlyList<double> Axes { get; init; } = EdgeRegularity.HexagonalAxes;

    /// <summary>Starting parameters for constructive pathways.</summary>
    public IsoLoopParams? Parameters { get; init; }

    /// <summary>A lower-is-better objective, for pathways that search; null disables them.</summary>
    public Func<string, CancellationToken, Task<double>>? Score { get; init; }

    /// <summary>Runs VTracer for the <c>autotraced</c> pathway; null makes that pathway an error.</summary>
    public IVtracer? Tracer { get; init; }
}

/// <summary>One named route to a candidate mark.</summary>
/// <param name="Key">The key used on the command line.</param>
/// <param name="Name">The label shown in tables.</param>
/// <param name="Description">What the pathway trades.</param>
/// <param name="Generate">Builds the candidate's SVG text.</param>
/// <param name="NeedsScoring">Whether the pathway searches and therefore needs a renderer.</param>
public sealed record Pathway(string Key, string Name, string Description, Func<PathwayContext, CancellationToken, Task<string>> Generate, bool NeedsScoring = false);

/// <summary>One scalar parameter the fitter may adjust.</summary>
/// <param name="Name">The parameter's name.</param>
/// <param name="Read">Reads its value.</param>
/// <param name="Write">Returns a copy with the value replaced.</param>
public sealed record FitField(string Name, Func<IsoLoopParams, double> Read, Func<IsoLoopParams, double, IsoLoopParams> Write);

/// <summary>The best parameters the fitter found and their loss.</summary>
/// <param name="Parameters">The parameters.</param>
/// <param name="Loss">Their loss against the reference.</param>
public sealed record FitResult(IsoLoopParams Parameters, double Loss);

/// <summary>The routes by which a brand master can be produced (image_drift's <c>pathways</c>); every one is deterministic.</summary>
public static class PathwayCatalog
{
    /// <summary>The pathway used when none is named.</summary>
    public const string DefaultPathway = "traced";

    /// <summary>The parameters the fitter searches, in a fixed order so runs are reproducible.</summary>
    public static IReadOnlyList<FitField> FitFields { get; } =
    [
        new("r_outer", p => p.ROuter, (p, v) => p with { ROuter = v }),
        new("r_mid", p => p.RMid, (p, v) => p with { RMid = v }),
        new("r_inner", p => p.RInner, (p, v) => p with { RInner = v }),
        new("depth", p => p.Depth, (p, v) => p with { Depth = v }),
        new("beam_half_width", p => p.BeamHalfWidth, (p, v) => p with { BeamHalfWidth = v }),
        new("beam_length", p => p.BeamLength, (p, v) => p with { BeamLength = v }),
    ];

    /// <summary>The step schedule: fixed rather than adaptive, again for reproducibility.</summary>
    public static IReadOnlyList<double> FitSteps { get; } = [16.0, 8.0, 4.0, 2.0];

    /// <summary>Every pathway by key, in presentation order.</summary>
    public static IReadOnlyDictionary<string, Pathway> All { get; } = new Dictionary<string, Pathway>
    {
        ["traced"] = new Pathway(
            "traced",
            "Traced",
            "The governed trace, unchanged. Highest fidelity, inherits the tracer's jitter.",
            (context, _) => Task.FromResult(context.MasterSvg)),
        ["autotraced"] = new Pathway(
            "autotraced",
            "Autotraced (VTracer)",
            "A fresh VTracer trace of the reference. The untuned baseline: close to the reference's pixels, worst geometry of the lot.",
            AutotracedAsync),
        ["regularized"] = new Pathway(
            "regularized",
            "Regularized trace",
            "The trace with its edges snapped onto the canonical axes. Hybrid.",
            (context, _) => Task.FromResult(EdgeRegularity.RegularizeSvg(context.MasterSvg, context.Axes))),
        ["parametric"] = new Pathway(
            "parametric",
            "Parametric",
            "Constructed from named geometry. Exact coordinates, unconstrained resemblance.",
            (context, _) => Task.FromResult(IsoLoopBuilder.GenerateSvg(parameters: context.Parameters ?? new IsoLoopParams()))),
        ["fitted"] = new Pathway(
            "fitted",
            "Fitted parametric",
            "Constructed from named geometry, dimensions fitted to the reference.",
            FittedAsync,
            NeedsScoring: true),
    }.ToFrozenDictionary();

    /// <summary>Auto-traces <paramref name="referencePath"/> with VTracer and returns the SVG text.</summary>
    internal static Task<string> TraceReferenceAsync(IVtracer tracer, string referencePath, CancellationToken cancellationToken)
    {
        return tracer.TraceAsync(Path.GetFullPath(referencePath), new VtracerConfig(), cancellationToken);
    }

    /// <summary>
    /// Fits constructive parameters to the reference by coordinate descent: fixed field order, fixed step schedule, no randomness, and
    /// ties broken toward the incumbent (only a strictly lower loss replaces it), so the same reference yields the same parameters.
    /// </summary>
    internal static async Task<FitResult> FitParametersAsync(PathwayContext context, IReadOnlyList<FitField>? fields = null, IReadOnlyList<double>? steps = null, CancellationToken cancellationToken = default)
    {
        var score = context.Score ?? throw new InvalidOperationException("expected a score function to fit parameters with, found none");
        var best = context.Parameters ?? new IsoLoopParams();
        var bestLoss = await score(IsoLoopBuilder.GenerateSvg(parameters: best), cancellationToken).ConfigureAwait(false);
        foreach (var step in steps ?? FitSteps)
        {
            foreach (var field in fields ?? FitFields)
            {
                var current = field.Read(best);
                foreach (var delta in new[] { step, -step })
                {
                    var candidateValue = current + delta;
                    if (candidateValue <= 1.0)
                    {
                        continue;
                    }

                    var candidate = field.Write(best, candidateValue);
                    var loss = await score(IsoLoopBuilder.GenerateSvg(parameters: candidate), cancellationToken).ConfigureAwait(false);
                    if (loss < bestLoss)
                    {
                        best = candidate;
                        bestLoss = loss;
                    }
                }
            }
        }

        return new FitResult(best, bestLoss);
    }

    private static Task<string> AutotracedAsync(PathwayContext context, CancellationToken cancellationToken)
    {
        var tracer = context.Tracer ?? throw new InvalidOperationException("expected a VTracer runner to trace the reference with, found none");
        return TraceReferenceAsync(tracer, context.ReferencePath, cancellationToken);
    }

    private static async Task<string> FittedAsync(PathwayContext context, CancellationToken cancellationToken)
    {
        var fit = await FitParametersAsync(context, cancellationToken: cancellationToken).ConfigureAwait(false);
        return IsoLoopBuilder.GenerateSvg(parameters: fit.Parameters);
    }
}
