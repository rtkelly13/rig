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

        _ = WatchParent(TakeParent(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable), () => Environment.Exit(ParentGoneExit));
        return rest;
    }

    /// <summary>
    /// Reads <see cref="ParentVariable"/> and removes it from this process's environment, so nothing rig starts
    /// inherits it. Otherwise a terminal or agent the logon run opened would pass the wrapper's PID to every rig it
    /// later starts, and each would exit <see cref="ParentGoneExit"/> silently once that wrapper had gone.
    /// </summary>
    /// <param name="read">Reads an environment variable.</param>
    /// <param name="write">Sets an environment variable; null removes it.</param>
    /// <returns>The parent's PID as set, or null.</returns>
    internal static string? TakeParent(Func<string, string?> read, Action<string, string?> write)
    {
        var pid = read(ParentVariable);
        if (pid is not null)
        {
            write(ParentVariable, null);
        }

        return pid;
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
