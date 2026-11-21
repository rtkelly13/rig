using System.Runtime.InteropServices;
using Rig.Core.Processes;

namespace Rig.Core.Jobs;

/// <summary>How the scheduled tasks start rig without leaving a console window on screen.</summary>
public enum JobsLauncher
{
    /// <summary>Through <c>pythonw</c>, which has no console and starts rig with none: nothing appears.</summary>
    Pythonw,

    /// <summary>rig itself, which frees the console Windows opens for it at once: a brief flash, and no Python.</summary>
    Direct,
}

/// <summary>The stable paths registered in Task Scheduler, through current for a runtime copy.</summary>
/// <param name="Root">The checkout or runtime/current directory.</param>
/// <param name="Plan">The guard's workspace plan.</param>
/// <param name="Rig">The published rig the tasks run.</param>
/// <param name="Pythonw">The <c>pythonw</c> that starts rig, or null when the tasks start rig directly.</param>
public sealed record JobsInstallTarget(string Root, string Plan, string Rig, string? Pythonw = null)
{
    /// <summary>The leading argument that tells rig to free its console before anything else runs.</summary>
    public const string DetachFlag = "--detach-console";

    /// <summary>The variable naming the process rig exits with, so stopping a task stops rig too.</summary>
    public const string ParentVariable = "RIG_PARENT_PID";

    /// <summary>The <c>pythonw -c</c> program: start rig with no console, tell it to exit with this wrapper, pass its exit code back.</summary>
    public const string Wrapper =
        "import os,subprocess,sys;os.environ['" + ParentVariable + "']=str(os.getpid());sys.exit(subprocess.call(sys.argv[1:],creationflags=0x08000000))";

    /// <summary>The program each task's action runs.</summary>
    public string Command => Pythonw ?? Rig;

    /// <summary>The action's arguments for a rig command line.</summary>
    public string Arguments(string rig) => Pythonw is null ? $"{DetachFlag} {rig}" : $"-I -c \"{Wrapper}\" \"{Rig}\" {rig}";

    /// <summary>Resolve the scheduler target using native platform intrinsics.</summary>
    public static Task<JobsInstallTarget> ResolveAsync(
        string checkout, string appData, string userPlan, IProc proc, JobsLauncher launcher, string? pythonw, CancellationToken cancellationToken = default) =>
        ResolveAsync(checkout, appData, userPlan, proc, launcher, pythonw,
            OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux, cancellationToken);

    /// <summary>Resolves the install paths, refuses linked worktrees and validates every target before changing tasks.</summary>
    /// <param name="checkout">The rig tree this process runs from.</param>
    /// <param name="appData">rig's per-user app data folder.</param>
    /// <param name="userPlan">The plan a checkout's guard reads.</param>
    /// <param name="proc">Runs git and uv.</param>
    /// <param name="launcher">How the tasks start rig.</param>
    /// <param name="pythonw">The <c>pythonw</c> to use; null asks uv for its managed Python.</param>
    /// <param name="cancellationToken">Stops the lookups.</param>
    /// <param name="platform">The target OS for inspection or portable tests; normal callers use native detection.</param>
    public static async Task<JobsInstallTarget> ResolveAsync(
        string checkout, string appData, string userPlan, IProc proc, JobsLauncher launcher, string? pythonw, OSPlatform platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proc);
        var windows = platform == OSPlatform.Windows;
        checkout = Path.GetFullPath(checkout);
        string root, plan, rig;
        if (JobsSite.CopyOf(Path.Join(checkout, JobsSite.PublishFolder), appData) is not null)
        {
            root = Path.Join(appData, JobsSite.RuntimeFolder, "current");
            plan = Path.Join(root, "desktop-manager", "workspace-plan.bundle.json");
            rig = Path.Join(root, JobsSite.PublishFolder, windows ? "rig.exe" : "rig");
        }
        else
        {
            var result = await proc.RunAsync(new ProcRequest(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"]) { WorkingDirectory = checkout }, cancellationToken).ConfigureAwait(false);
            if (!result.Ok && (File.Exists(Path.Join(checkout, ".git")) || Directory.Exists(Path.Join(checkout, ".git"))))
            {
                throw new InvalidOperationException("Cannot verify the main checkout; git rev-parse failed. No tasks were changed.");
            }
            if (result.Ok && result.OutOrEmpty.Length > 0 && Path.GetDirectoryName(result.OutOrEmpty) is { } main
                && Path.GetRelativePath(checkout, main) != ".")
            {
                throw new InvalidOperationException($"jobs install must run from the main checkout or a runtime copy, not a worktree.\nMain checkout: {main}");
            }

            (root, plan, rig) = (checkout, userPlan, Path.Join(checkout, "temp", "jobs-runtime", windows ? "rig.exe" : "rig"));
        }

        foreach (var path in windows ? new[] { rig, plan } : [rig])
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"jobs install target is missing: {path}. Install a runtime copy, or publish src/Rig.Cli to temp/jobs-runtime in the main checkout.");
            }
        }

        if (!windows || launcher == JobsLauncher.Direct)
        {
            return new(root, plan, rig);
        }

        pythonw ??= await ManagedPythonwAsync(proc, appData, cancellationToken).ConfigureAwait(false);
        return File.Exists(pythonw)
            ? new(root, plan, rig, Path.GetFullPath(pythonw))
            : throw new InvalidOperationException($"pythonw is missing: {pythonw}. Pass --pythonw <path>, or --launcher direct to run without Python.");
    }

    /// <summary>
    /// The <c>pythonw</c> beside uv's default managed Python, found outside any project from <paramref name="appData"/>;
    /// its minor-version folder survives patch upgrades.
    /// </summary>
    /// <exception cref="InvalidOperationException">uv has no managed Python.</exception>
    public static async Task<string> ManagedPythonwAsync(IProc proc, string appData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proc);
        var found = await proc.RunAsync(new ProcRequest(["uv", "python", "find", "--managed-python", "--no-project"]) { WorkingDirectory = appData }, cancellationToken).ConfigureAwait(false);
        return found.OutOrEmpty.Length > 0 && Path.GetDirectoryName(found.OutOrEmpty) is { } folder
            ? Path.Join(folder, "pythonw.exe")
            : throw new InvalidOperationException("No pythonw found: uv has no managed Python (`uv python install`). Pass --pythonw <path>, or --launcher direct to run without Python.");
    }
}
