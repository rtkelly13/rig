namespace Rig.Core.Jobs;

/// <summary>
/// Where the jobs' plan and personal root come from, and what their environment gains, for the rig the jobs run from.
/// </summary>
/// <param name="Plan">The workspace plan the restore, session and align jobs read.</param>
/// <param name="PersonalRoot">The personal root passed to <c>estate</c>.</param>
/// <param name="Environment">Variables each job gets unless the run already has them.</param>
public sealed record JobsSite(string Plan, string PersonalRoot, IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The folder under the app data folder that holds the runtime copies.</summary>
    public const string RuntimeFolder = "runtime";

    /// <summary>Restore's log folder variable.</summary>
    public const string LogDirVariable = "DESKTOP_MANAGER_LOG_DIR";

    /// <summary>The trace folder variable, <c>rig.tracing.TRACE_HOME_ENV</c>.</summary>
    public const string TraceHomeVariable = "SHARED_UTILITIES_TRACE_HOME";

    /// <summary>The folder inside a runtime copy that holds the published <c>rig</c>.</summary>
    public const string PublishFolder = "rig";

    /// <summary>The main checkout containing a development task publish under temp/jobs-runtime, else null.</summary>
    public static string? PublishedCheckout(string baseDirectory)
    {
        var publish = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var temp = Path.GetDirectoryName(publish);
        var root = temp is null ? null : Path.GetDirectoryName(temp);
        return Path.GetFileName(publish) == "jobs-runtime" && Path.GetFileName(temp) == "temp" && root is not null
            && File.Exists(Path.Join(root, "src", "Rig.Cli", "Rig.Cli.csproj")) ? root : null;
    }

    /// <summary>
    /// The runtime copy a rig published at <paramref name="baseDirectory"/> belongs to: the copy folder when it is
    /// <c>&lt;<paramref name="appData"/>&gt;/runtime/&lt;copy&gt;/rig</c>, reached by its own name or the <c>current</c>
    /// junction; else null, a rig built or installed anywhere else.
    /// </summary>
    /// <param name="baseDirectory">The folder the running assembly was loaded from.</param>
    /// <param name="appData">rig's per-user app data folder.</param>
    public static string? CopyOf(string baseDirectory, string appData)
    {
        var publish = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        var copy = Path.GetDirectoryName(publish);
        var runtime = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Join(appData, RuntimeFolder)));
        return copy is not null
            && string.Equals(Path.GetFileName(publish), PublishFolder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(copy), runtime, StringComparison.OrdinalIgnoreCase)
            ? copy
            : null;
    }

    /// <summary>A published runtime's stable executable path, so service registrations follow later updates.</summary>
    public static string? StablePublishedRig(string baseDirectory, string appData, bool windows) =>
        CopyOf(baseDirectory, appData) is null ? null
            : Path.Join(appData, RuntimeFolder, "current", PublishFolder, windows ? "rig.exe" : "rig");

    /// <summary>
    /// The site for a rig at <paramref name="checkout"/>: a runtime copy when it sits under
    /// <c>&lt;<paramref name="appData"/>&gt;/runtime</c>, else a checkout using <paramref name="userPlan"/> and
    /// <paramref name="personalRoot"/>.
    /// </summary>
    /// <param name="checkout">The rig tree this process runs from.</param>
    /// <param name="appData">rig's per-user app data folder.</param>
    /// <param name="userPlan">The per-user workspace plan.</param>
    /// <param name="personalRoot">The personal root under the code root.</param>
    public static JobsSite Locate(string checkout, string appData, string userPlan, string personalRoot)
    {
        var runtime = Path.Join(appData, RuntimeFolder);
        if (!Under(checkout, runtime))
        {
            return new JobsSite(userPlan, personalRoot, new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var plan = Path.Join(runtime, "current", "desktop-manager", "workspace-plan.bundle.json");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LogDirVariable] = Path.Join(appData, "workspace-restore", "logs"),
            [TraceHomeVariable] = Path.Join(appData, "traces"),
        };
        return new JobsSite(plan, personalRoot, environment);
    }

    // Path.GetRelativePath compares as the file system does: without case on Windows and macOS.
    private static bool Under(string path, string folder)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(folder), Path.GetFullPath(path));
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
