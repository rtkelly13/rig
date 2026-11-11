using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Core.Otel;

/// <summary>Whether a pid exists, and asking it to stop.</summary>
public interface IProcessSignals
{
    /// <summary>Whether <paramref name="pid"/> exists (another user's process counts).</summary>
    bool Alive(int pid);

    /// <summary>Sends SIGTERM, or SIGKILL when <paramref name="kill"/>; a pid that has gone is ignored.</summary>
    void Signal(int pid, bool kill = false);
}

/// <summary>
/// The libc <c>kill</c>, as the Python's <c>os.kill</c>. Windows has neither signal 0 nor SIGTERM, and nothing the
/// ClickHouse stack starts runs there, so there a pid is never alive and is never signalled.
/// </summary>
public sealed partial class ProcessSignals : IProcessSignals
{
    private const int SigKill = 9;
    private const int SigTerm = 15;
    private const int NoSuchProcess = 3;

    /// <inheritdoc/>
    public bool Alive(int pid)
    {
        if (OperatingSystem.IsWindows() || pid <= 0)
        {
            return false;
        }

        // ESRCH: no such process. EPERM: it exists but belongs to someone else.
        return Kill(pid, 0) == 0 || Marshal.GetLastPInvokeError() != NoSuchProcess;
    }

    /// <inheritdoc/>
    public void Signal(int pid, bool kill = false)
    {
        // A pid of 0 or less would signal a whole process group.
        if (!OperatingSystem.IsWindows() && pid > 0)
        {
            _ = Kill(pid, kill ? SigKill : SigTerm);
        }
    }

    [UnsupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
