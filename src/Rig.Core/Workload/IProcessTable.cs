namespace Rig.Core.Workload;

/// <summary>One live process, as the workload sampler sees it.</summary>
/// <param name="Pid">Its process id.</param>
/// <param name="ParentPid">The id it was started under. The parent may have exited since, and Windows reuses ids.</param>
/// <param name="Name">The executable's file name, e.g. <c>claude.exe</c> or <c>Google Chrome</c>.</param>
/// <param name="Path">The executable's full path, or empty when it could not be read.</param>
/// <param name="PrivateBytes">Committed private memory (Windows' private bytes); resident memory on macOS.</param>
/// <param name="WorkingSetBytes">Physical memory in use (Windows' working set; RSS on macOS).</param>
/// <param name="Started">When it started, in UTC, or null when unknown (Windows' Idle and System).</param>
public sealed record ProcessRow(int Pid, int ParentPid, string Name, string Path, long PrivateBytes, long WorkingSetBytes, DateTime? Started);

/// <summary>The machine's physical memory.</summary>
/// <param name="TotalBytes">Installed memory the OS can use.</param>
/// <param name="AvailableBytes">Memory free for new work now: Windows' available, macOS' free plus inactive and speculative pages.</param>
public sealed record MemoryStatus(long TotalBytes, long AvailableBytes);

/// <summary>Every process and the memory totals, read at one moment.</summary>
/// <param name="Processes">The processes.</param>
/// <param name="Memory">The machine's memory.</param>
public sealed record ProcessSnapshot(IReadOnlyList<ProcessRow> Processes, MemoryStatus Memory);

/// <summary>
/// The port onto the machine's process table. Reading it is the sampler's whole cost, so each platform reads it with one
/// cheap call (<see cref="WindowsProcessTable"/>, <see cref="MacProcessTable"/>). Command lines can hold secrets and cost
/// a call each on Windows, so they are read separately, only for the processes a sample reports by name.
/// </summary>
public interface IProcessTable
{
    /// <summary>Every live process and the machine's memory.</summary>
    Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>The command lines of <paramref name="pids"/>; a process that exited or cannot be read is left out.</summary>
    Task<IReadOnlyDictionary<int, string>> CommandLinesAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken = default);
}
