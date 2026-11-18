using System.Diagnostics;
using System.Runtime.InteropServices;
using Rig.Core.Jobs;

namespace Rig.Cli;

/// <summary>
/// How a scheduled task's launcher hands a run to rig. A task that starts rig directly passes <see cref="DetachFlag"/>
/// first, and rig frees the console window Windows opened for it. A task that starts rig through <c>pythonw</c> sets
/// <see cref="ParentVariable"/>, and rig exits when that wrapper does, so stopping the task stops rig too.
/// </summary>
internal static partial class ScheduledLaunch
{
    /// <summary>The leading argument that frees rig's console before anything else runs.</summary>
    public const string DetachFlag = JobsInstallTarget.DetachFlag;

    /// <summary>The variable naming the process rig must not outlive.</summary>
    public const string ParentVariable = JobsInstallTarget.ParentVariable;

    /// <summary>The exit code when the launching wrapper is gone.</summary>
    public const int ParentGoneExit = 3;

    /// <summary>Applies both handoffs and returns the arguments rig's commands see.</summary>
    public static string[] Apply(string[] args)
    {
        var (rest, detach) = Split(args);
        if (detach && OperatingSystem.IsWindows())
        {
            _ = FreeConsole();
        }

        _ = WatchParent(Environment.GetEnvironmentVariable(ParentVariable), () => Environment.Exit(ParentGoneExit));
        return rest;
    }

    /// <summary>The arguments without a leading <see cref="DetachFlag"/>, and whether it was there.</summary>
    internal static (string[] Args, bool Detach) Split(string[] args) =>
        args is [DetachFlag, .. var rest] ? (rest, true) : (args, false);

    /// <summary>
    /// Calls <paramref name="gone"/> when the process <paramref name="pid"/> names exits, or at once when it has
    /// already gone. Returns whether a parent was named.
    /// </summary>
    internal static bool WatchParent(string? pid, Action gone)
    {
        if (!int.TryParse(pid, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            return false;
        }

        try
        {
            var parent = Process.GetProcessById(id);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => gone();
            if (parent.HasExited)
            {
                gone();
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            gone();
        }

        return true;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}
