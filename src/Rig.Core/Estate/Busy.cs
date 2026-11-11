using Rig.Core.Processes;
using Rig.Core.Roots;

namespace Rig.Core.Estate;

/// <summary>
/// One snapshot of the folders processes are working in, taken once per run and asked many times. Ported from
/// <c>src/rig/estate/busy.py</c> and the <c>Busy</c> of <c>src/rig/estate/cleanup.py</c>.
/// </summary>
/// <remarks>
/// A worktree with a shell, an agent session or an Orca terminal sitting in it is in use however finished its branch
/// looks, and removing it pulls the folder out from under that process. Measured on this estate on 2026-11-06:
/// <c>estate tidy --merged</c> would have removed 62 worktrees, 8 of them with live Orca terminals. A process's working
/// folder covers a plain shell or a <c>claude</c> session started outside Orca too, and needs no running app. One
/// <c>lsof</c> call answers for every path: it takes seconds, so it is never repeated per worktree.
/// </remarks>
/// <param name="Dirs">Every process's working folder; null when that could not be read, which is not "nothing is busy".</param>
public sealed record Busy(IReadOnlyList<string>? Dirs)
{
    /// <summary>A snapshot that could not be read: every removal is refused unless forced.</summary>
    public static readonly Busy Unknown = new((IReadOnlyList<string>?)null);

    /// <summary>How long <c>lsof</c> may take.</summary>
    public static readonly TimeSpan LsofTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The working folders of every process, read with one <c>lsof</c>.</summary>
    public static Task<Busy> SnapshotAsync(IProc proc, CancellationToken cancellationToken = default) =>
        SnapshotAsync(proc, OperatingSystem.IsWindows(), cancellationToken);

    /// <summary>
    /// The working folders of every process; <see cref="Dirs"/> is null on Windows, with no <c>lsof</c>, or when it
    /// fails having printed nothing. lsof exits 1 when some process could not be read, yet still prints the rest.
    /// </summary>
    /// <param name="proc">Runs <c>lsof</c>.</param>
    /// <param name="windows">
    /// No lsof answers there: an MSYS one would print paths nothing matches, which reads as "nothing is busy".
    /// </param>
    /// <param name="cancellationToken">Stops the call.</param>
    public static async Task<Busy> SnapshotAsync(IProc proc, bool windows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proc);
        if (windows)
        {
            return Unknown;
        }

        var ran = await proc.RunAsync(new ProcRequest(["lsof", "-n", "-d", "cwd", "-Fn"]) { Timeout = LsofTimeout }, cancellationToken).ConfigureAwait(false);
        if (ran.Code == Ran.NotFound || (!ran.Ok && ran.Out.Length == 0))
        {
            return Unknown;
        }

        return new Busy([.. ran.Out.ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith("n/", StringComparison.Ordinal)).Select(line => line[1..])]);
    }

    /// <summary>Why <paramref name="path"/> must not be removed while in use; empty when it may be.</summary>
    /// <param name="path">The folder about to go.</param>
    /// <param name="force">Proceed when the snapshot could not be read.</param>
    public string Refusal(string path, bool force)
    {
        if (Dirs is null)
        {
            return force ? string.Empty : "cannot tell whether a process is working in it (lsof unavailable) — --force to skip";
        }

        // No flag overrides this: removing it pulls the folder out from under a live process.
        return InUse(path, Dirs) ? "a process is working in it (a shell, agent or Orca terminal)" : string.Empty;
    }

    /// <summary>Whether any of <paramref name="dirs"/> is <paramref name="path"/>, links resolved, or lies below it.</summary>
    public static bool InUse(string path, IEnumerable<string> dirs)
    {
        ArgumentNullException.ThrowIfNull(dirs);
        var target = PathResolution.Resolve(path);
        return dirs.Any(cwd => PathResolution.IsUnder(cwd, target));
    }
}
