using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Core.Workload;

/// <summary>
/// The Windows process table. One <c>NtQuerySystemInformation</c> call returns every process's ids, private bytes, working
/// set and start time (<see cref="SystemProcessInformation"/>); WMI's <c>Win32_Process</c> would return the same through a
/// COM service at many times the cost, and <see cref="System.Diagnostics.Process"/> has neither the parent id nor the
/// command line. Image paths come from <c>QueryFullProcessImageNameW</c> with limited query rights, so the app list can tell
/// two executables with one name apart; command lines from <c>NtQueryInformationProcess</c>, only for the pids asked for.
/// A process this user cannot open (elevated, protected) keeps an empty path and no command line.
/// </summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the parsing it relies on is SystemProcessInformation, tested on every OS.")]
public sealed partial class WindowsProcessTable : IProcessTable
{
    private const uint QueryLimitedInformation = 0x1000;
    private const int CommandLineClass = 60;
    private const int PathCapacity = 32768;

    /// <inheritdoc/>
    public Task<ProcessSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var path = new char[PathCapacity];
        var rows = Processes().Select(row => row with { Path = ImagePath(row.Pid, path) ?? string.Empty }).ToList();
        return Task.FromResult(new ProcessSnapshot(rows, Memory()));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<int, string>> CommandLinesAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pids);
        var lines = new Dictionary<int, string>();
        foreach (var pid in pids)
        {
            if (CommandLine(pid) is { Length: > 0 } line)
            {
                lines[pid] = line;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<int, string>>(lines);
    }

    private static IReadOnlyList<ProcessRow> Processes()
    {
        var size = 1 << 20;
        while (true)
        {
            var buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
            var at = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
            var status = NtQuerySystemInformation(SystemProcessInformation.ProcessListClass, at, size, out var needed);
            if (status == SystemProcessInformation.InfoLengthMismatch)
            {
                size = Math.Max(size * 2, needed + (64 * 1024));
                continue;
            }

            return status < 0
                ? throw new InvalidOperationException($"NtQuerySystemInformation failed with 0x{status.ToString("X8", CultureInfo.InvariantCulture)}")
                : SystemProcessInformation.Parse(buffer.AsSpan(0, needed), at);
        }
    }

    private static MemoryStatus Memory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? new MemoryStatus((long)status.TotalPhys, (long)status.AvailPhys)
            : throw new InvalidOperationException($"GlobalMemoryStatusEx failed with {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}");
    }

    private static string? ImagePath(int pid, char[] path)
    {
        if (pid <= 4)
        {
            return null;
        }

        var process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var size = (uint)path.Length;
            return QueryFullProcessImageNameW(process, 0, path, ref size) && size > 0 ? new string(path, 0, (int)size) : null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private static string? CommandLine(int pid)
    {
        var process = OpenProcess(QueryLimitedInformation, false, (uint)pid);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var size = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = GC.AllocateUninitializedArray<byte>(size, pinned: true);
                var at = Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0);
                var status = NtQueryInformationProcess(process, CommandLineClass, at, size, out var needed);
                if (status == SystemProcessInformation.InfoLengthMismatch && needed > size)
                {
                    size = needed;
                    continue;
                }

                return status < 0 ? null : SystemProcessInformation.UnicodeString(buffer.AsSpan(0, Math.Min(needed, size)), at);
            }

            return null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returnLength);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NtQueryInformationProcess(nint process, int informationClass, nint buffer, int length, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, [Out] char[] path, ref uint size);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
