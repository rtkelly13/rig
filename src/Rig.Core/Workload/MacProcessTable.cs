using System.Globalization;
using System.Text.RegularExpressions;
using Rig.Core.Processes;

namespace Rig.Core.Workload;

/// <summary>
/// The macOS process table: one <c>ps</c> for every process, <c>sysctl</c> for installed memory and <c>vm_stat</c> for what
/// is free. macOS has no cheap per-process private-bytes counter outside <c>libproc</c>, so both memory figures are the
/// resident size <c>ps</c> reports. Programs are named by absolute path because a LaunchAgent runs with no shell
/// <c>PATH</c>, and <c>LC_ALL=C</c> keeps <c>lstart</c> in the English format parsed here.
/// </summary>
/// <param name="proc">Runs <c>ps</c>, <c>sysctl</c> and <c>vm_stat</c>.</param>
/// <param name="zone">The zone <c>ps</c> prints start times in (the machine's local zone).</param>
public sealed partial class MacProcessTable(IProc proc, TimeZoneInfo zone) : IProcessTable
{
    /// <summary><c>ps</c>'s absolute path.</summary>
    public const string Ps = "/bin/ps";

    /// <summary><c>sysctl</c>'s absolute path.</summary>
    public const string Sysctl = "/usr/sbin/sysctl";

    /// <summary><c>vm_stat</c>'s absolute path.</summary>
    public const string VmStat = "/usr/bin/vm_stat";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc/>
    public async Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var table = await RunAsync([Ps, "-axo", "pid=,ppid=,rss=,lstart=,comm="], cancellationToken).ConfigureAwait(false);
        var total = await RunAsync([Sysctl, "-n", "hw.memsize"], cancellationToken).ConfigureAwait(false);
        var pages = await RunAsync([VmStat], cancellationToken).ConfigureAwait(false);
        return new ProcessSnapshot(ParseTable(table, zone), new MemoryStatus(long.Parse(total.Trim(), NumberStyles.None, CultureInfo.InvariantCulture), Available(pages)));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<int, string>> CommandLinesAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pids);
        if (pids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var list = string.Join(',', pids.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
        // ps exits 1 when one of the pids has gone; what it printed for the others still counts.
        var ran = await proc.RunAsync(Request([Ps, "-o", "pid=,command=", "-p", list]), cancellationToken).ConfigureAwait(false);
        return ParseCommands(ran.Out);
    }

    /// <summary>The rows of <c>ps -axo pid=,ppid=,rss=,lstart=,comm=</c> output, start times read in <paramref name="zone"/>.</summary>
    public static IReadOnlyList<ProcessRow> ParseTable(string text, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(zone);
        var rows = new List<ProcessRow>();
        foreach (var line in text.Split('\n'))
        {
            if (TableLine().Match(line) is not { Success: true } match)
            {
                continue;
            }

            var command = match.Groups["comm"].Value.TrimEnd();
            var resident = long.Parse(match.Groups["rss"].Value, CultureInfo.InvariantCulture) * 1024;
            rows.Add(new ProcessRow(
                int.Parse(match.Groups["pid"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["ppid"].Value, CultureInfo.InvariantCulture),
                command.Contains('/', StringComparison.Ordinal) ? command[(command.LastIndexOf('/') + 1)..] : command,
                command.Contains('/', StringComparison.Ordinal) ? command : string.Empty,
                resident,
                resident,
                Started(match.Groups["start"].Value, zone)));
        }

        return rows;
    }

    /// <summary>Bytes free for new work in <c>vm_stat</c> output: free, inactive and speculative pages.</summary>
    public static long Available(string vmStat)
    {
        ArgumentNullException.ThrowIfNull(vmStat);
        var pageSize = PageSize().Match(vmStat) is { Success: true } size ? long.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : 4096;
        long pages = 0;
        foreach (Match counter in Counter().Matches(vmStat))
        {
            if (counter.Groups["name"].Value is "free" or "inactive" or "speculative")
            {
                pages += long.Parse(counter.Groups["pages"].Value, CultureInfo.InvariantCulture);
            }
        }

        return pages * pageSize;
    }

    /// <summary>The command lines in <c>ps -o pid=,command=</c> output, by pid.</summary>
    public static IReadOnlyDictionary<int, string> ParseCommands(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new Dictionary<int, string>();
        foreach (var line in text.Split('\n'))
        {
            if (CommandLine().Match(line) is { Success: true } match)
            {
                lines[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)] = match.Groups[2].Value.TrimEnd();
            }
        }

        return lines;
    }

    private static DateTime? Started(string text, TimeZoneInfo zone)
    {
        var spaced = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return DateTime.TryParseExact(spaced, "ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone)
            : null;
    }

    private static ProcRequest Request(IReadOnlyList<string> argv) =>
        new(argv) { Timeout = Timeout, Environment = new Dictionary<string, string> { ["LC_ALL"] = "C" } };

    private async Task<string> RunAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken) =>
        (await proc.RunAsync(Request(argv), cancellationToken).ConfigureAwait(false)).EnsureOk(argv).Out;

    [GeneratedRegex(@"^\s*(?<pid>\d+)\s+(?<ppid>\d+)\s+(?<rss>\d+)\s+(?<start>\w{3}\s+\w{3}\s+\d{1,2}\s+\d{1,2}:\d{2}:\d{2}\s+\d{4})\s+(?<comm>\S.*)$")]
    private static partial Regex TableLine();

    [GeneratedRegex(@"page size of (\d+) bytes")]
    private static partial Regex PageSize();

    [GeneratedRegex(@"^Pages (?<name>[a-z ]+?):\s+(?<pages>\d+)\.?\s*$", RegexOptions.Multiline)]
    private static partial Regex Counter();

    [GeneratedRegex(@"^\s*(\d+)\s+(\S.*)$")]
    private static partial Regex CommandLine();
}
