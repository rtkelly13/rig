using System.Globalization;

namespace Rig.Core.Workload;

/// <summary>
/// Reads a run of samples back as the analysis docs/workload.md walks through: how low free memory went and when, the
/// peak and who held it, what Claude Code sessions cost, which single processes were large and whether they grew, and
/// which names burst. Pure: samples in, lines out.
/// </summary>
public static class WorkloadSummary
{
    private const double MbPerGb = 1024d;

    /// <summary>The summary of <paramref name="samples"/> (oldest first), with times in <paramref name="zone"/>.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<WorkloadSample> samples, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(zone);
        if (samples.Count == 0)
        {
            return ["No samples in that window."];
        }

        string At(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var first = samples[0];
        var last = samples[^1];
        var lowest = samples.MinBy(s => s.Ram.FreeMb)!;
        var peak = samples.MaxBy(s => s.Processes.PrivateMb)!;
        var lines = new List<string>
        {
            Invariant($"{samples.Count} samples from {At(first.Ts)} to {At(last.Ts)} on {string.Join(", ", samples.Select(s => s.Host).Distinct(StringComparer.Ordinal))}"),
            Invariant($"Free RAM: {Gb(lowest.Ram.FreeMb)}–{Gb(samples.Max(s => s.Ram.FreeMb))} GB of {Gb(last.Ram.TotalMb)} GB; lowest at {At(lowest.Ts)}"),
            Invariant($"Peak private total: {Gb(peak.Processes.PrivateMb)} GB at {At(peak.Ts)} ({peak.Processes.Count} processes)"),
            "Apps at the peak (private, working set, processes):",
        };
        lines.AddRange(peak.Apps.Select(app => Invariant($"  {app.App,-18} {Gb(app.PrivateMb),6} GB {Gb(app.WorkingSetMb),6} GB {app.Processes,5}")));
        lines.Add(Sessions(samples, At));
        lines.AddRange(Large(samples, At));
        lines.AddRange(Bursts(samples));
        return lines;
    }

    private static string Gb(long mb) => (mb / MbPerGb).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Sessions(IReadOnlyList<WorkloadSample> samples, Func<DateTime, string> at)
    {
        var withSessions = samples.Where(s => s.Claude.SessionMb is not null).ToList();
        if (withSessions.Count == 0)
        {
            return "Claude Code sessions: none seen.";
        }

        var most = samples.MaxBy(s => s.Claude.Sessions)!;
        var median = Attribution.SpreadOf([.. withSessions.Select(s => s.Claude.SessionMb!.Median)])!.Median;
        var biggestTree = withSessions.Max(s => s.Claude.TreeMb?.Max ?? 0);
        return Invariant(
            $"Claude Code sessions: {samples.Min(s => s.Claude.Sessions)}–{most.Claude.Sessions} (most at {at(most.Ts)}); each {withSessions.Min(s => s.Claude.SessionMb!.Min):N0}–{withSessions.Max(s => s.Claude.SessionMb!.Max):N0} MB, median {median:N0} MB; largest session tree {Gb(biggestTree)} GB");
    }

    private static List<string> Large(IReadOnlyList<WorkloadSample> samples, Func<DateTime, string> at)
    {
        var seen = samples
            .SelectMany(sample => sample.Large.Select(process => (sample.Ts, Process: process)))
            .GroupBy(entry => (entry.Process.Pid, entry.Process.Name))
            .Select(group => group.OrderBy(entry => entry.Ts).ToList())
            .OrderByDescending(entries => entries.Max(entry => Math.Max(entry.Process.PrivateMb, entry.Process.WorkingSetMb)))
            .ToList();
        if (seen.Count == 0)
        {
            return ["Large processes: none."];
        }

        var lines = new List<string> { "Large processes (first → last private MB, peak, growth):" };
        foreach (var entries in seen)
        {
            var (start, head) = entries[0];
            var (end, tail) = entries[^1];
            var hours = (end - start).TotalHours;
            var growth = hours >= 1 ? Invariant($", {(tail.PrivateMb - head.PrivateMb) / hours:+0;-0;0} MB/h") : string.Empty;
            lines.Add(Invariant(
                $"  {head.Name} ({head.Pid}, {head.App}) {head.PrivateMb:N0} → {tail.PrivateMb:N0}, peak {entries.Max(e => e.Process.PrivateMb):N0}, {at(start)} to {at(end)}{growth}"));
        }

        return lines;
    }

    private static List<string> Bursts(IReadOnlyList<WorkloadSample> samples)
    {
        var bursts = samples
            .SelectMany(sample => sample.Bursts)
            .GroupBy(burst => burst.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Name: group.Key, Top: group.MaxBy(burst => burst.Count)!, Samples: group.Count()))
            .OrderByDescending(burst => burst.Top.Count)
            .ThenBy(burst => burst.Name, StringComparer.Ordinal)
            .ToList();
        return bursts.Count == 0
            ? ["Bursts: none."]
            : ["Bursts (most copies, owner, peak private MB, samples seen in):",
                .. bursts.Select(burst => Invariant($"  {burst.Name} ×{burst.Top.Count} ({burst.Top.App}) {burst.Top.PrivateMb:N0} MB in {burst.Samples}"))];
    }
}
