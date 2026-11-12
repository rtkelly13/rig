namespace Rig.Core.Workload;

/// <summary>What one <c>workload sample</c> did.</summary>
/// <param name="Sample">The sample taken.</param>
/// <param name="File">The day file it was appended to.</param>
/// <param name="Bytes">The line's length.</param>
/// <param name="Pruned">Day files deleted for being over a week old.</param>
public sealed record SampleResult(WorkloadSample Sample, string File, int Bytes, IReadOnlyList<string> Pruned);

/// <summary>
/// Takes one sample: reads the process table, fetches command lines for the large processes only (the expensive part),
/// attributes memory, appends the line, and prunes old day files.
/// </summary>
/// <param name="Table">The process table.</param>
/// <param name="Rules">The app list.</param>
/// <param name="Paths">Where samples go.</param>
/// <param name="Limits">Thresholds and caps.</param>
public sealed record WorkloadSampler(IProcessTable Table, AppRules Rules, WorkloadPaths Paths, SampleLimits Limits)
{
    /// <summary>Samples at <paramref name="now"/> on <paramref name="host"/> (<paramref name="os"/>), appends it and prunes.</summary>
    public async Task<SampleResult> RunAsync(DateTimeOffset now, string host, string os, CancellationToken cancellationToken = default)
    {
        var snapshot = await Table.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var commands = await Table.CommandLinesAsync(Attribution.LargePids(snapshot, Limits), cancellationToken).ConfigureAwait(false);
        var sample = Attribution.Build(snapshot, Rules, commands, Limits, now, host, os);
        var (file, bytes) = Paths.Append(sample);
        return new SampleResult(sample, file, bytes, Paths.Prune(now, apply: true));
    }
}
