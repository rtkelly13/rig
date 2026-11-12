using Rig.Core.Services;

namespace Rig.Core.Workload;

/// <summary>
/// The macOS LaunchAgent that samples every 15 minutes. Modelled on <see cref="Otel.OtelService"/> but scheduled rather
/// than resident: launchd starts <c>rig workload sample</c> every <see cref="IntervalSeconds"/> and at load, and nothing
/// stays running between ticks. <c>ProcessType</c> Background, <c>LowPriorityIO</c> and <c>Nice</c> keep it housekeeping.
/// launchd gives agents no shell PATH, so the plist sets one that finds <c>rig</c>'s shim and <c>dotnet</c>.
/// </summary>
public static class WorkloadService
{
    /// <summary>The agent's label.</summary>
    public const string Label = "dev.rtkelly.rig.workload";

    /// <summary>Seconds between samples.</summary>
    public const int IntervalSeconds = 900;

    /// <summary>The plist's file name in the LaunchAgents folder.</summary>
    public const string PlistName = Label + ".plist";

    /// <summary>Where launchd agents look for programs after the folders of rig's own tools.</summary>
    public static readonly IReadOnlyList<string> SystemPath = ["/usr/local/bin", "/opt/homebrew/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"];

    /// <summary>The PATH the agent runs with: the folders of <paramref name="tools"/> (in order, once each), then <see cref="SystemPath"/>.</summary>
    public static string PathFor(IEnumerable<string> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        // POSIX paths whatever OS renders the plist, so tests on Windows see what a Mac writes.
        var folders = tools.Select(tool => tool.LastIndexOf('/') is var slash and > 0 ? tool[..slash] : null).OfType<string>();
        return string.Join(':', folders.Concat(SystemPath).Distinct(StringComparer.Ordinal));
    }

    /// <summary>The agent running <paramref name="rig"/> <c>workload sample</c> with <paramref name="path"/> as PATH, both outputs to <paramref name="log"/>.</summary>
    public static byte[] Render(string rig, string path, string log) =>
        Plist.Render(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Label"] = Label,
            ["ProgramArguments"] = new[] { rig, "workload", "sample" },
            ["StartInterval"] = IntervalSeconds,
            ["RunAtLoad"] = true,
            ["ProcessType"] = "Background",
            ["LowPriorityIO"] = true,
            ["Nice"] = 10,
            ["EnvironmentVariables"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = path },
            ["StandardOutPath"] = log,
            ["StandardErrorPath"] = log,
        });

    /// <summary>Where the agent's plist lives in <paramref name="launchd"/>'s agents folder.</summary>
    public static string PlistPath(Launchd launchd)
    {
        ArgumentNullException.ThrowIfNull(launchd);
        return Path.Join(launchd.AgentsDir, PlistName);
    }

    /// <summary>Whether the agent's plist is in place, loaded or not.</summary>
    public static bool Installed(Launchd launchd) => File.Exists(PlistPath(launchd));

    /// <summary>Whether launchd has the agent loaded.</summary>
    public static Task<bool> LoadedAsync(Launchd launchd, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchd);
        return launchd.LoadedAsync(Label, cancellationToken);
    }

    /// <summary>Writes the agent and loads it; returns the plist's path.</summary>
    /// <exception cref="Processes.ProcException"><c>launchctl bootstrap</c> failed.</exception>
    public static async Task<string> InstallAsync(Launchd launchd, WorkloadPaths paths, string rig, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchd);
        ArgumentNullException.ThrowIfNull(paths);
        var plist = PlistPath(launchd);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(paths.Log)!);
        await launchd.LoadAsync(plist, Render(rig, path, paths.Log), cancellationToken).ConfigureAwait(false);
        return plist;
    }

    /// <summary>Unloads and deletes the agent; false when it was not installed.</summary>
    public static Task<bool> UninstallAsync(Launchd launchd, CancellationToken cancellationToken = default) =>
        launchd is null ? throw new ArgumentNullException(nameof(launchd)) : launchd.RemoveAsync(PlistPath(launchd), cancellationToken);
}
