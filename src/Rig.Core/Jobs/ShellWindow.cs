using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Core.Jobs;

/// <summary>Explorer's shell window, which the virtual-desktop APIs need before a logon restore can run.</summary>
[ExcludeFromCodeCoverage(Justification = "Win32 interop; the ubuntu coverage run cannot reach it, and the scheduled run takes it through a seam.")]
public static partial class ShellWindow
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);

    /// <summary>Waits up to <paramref name="timeout"/> for Explorer to own a shell window; false on timeout. True at once off Windows.</summary>
    public static bool WaitFor(TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (Native.GetShellWindow() != 0)
            {
                return true;
            }

            Thread.Sleep(Poll);
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static partial class Native
    {
        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial nint GetShellWindow();
    }
}
