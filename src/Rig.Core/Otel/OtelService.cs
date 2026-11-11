using Rig.Core.Services;

namespace Rig.Core.Otel;

/// <summary>
/// The macOS LaunchAgent that keeps the collector running. Ported from <c>rig.otel_stack.service</c>. Unlike a scheduled
/// agent this one is resident (<c>KeepAlive</c>), because tools send telemetry whenever they run; that is affordable only
/// because the collector at rest is measured at 3 MB resident and 0.03 s of CPU per two idle minutes. <c>ProcessType</c>
/// Background, <c>LowPriorityIO</c> and <c>Nice</c> keep macOS treating it as housekeeping, and <c>ThrottleInterval</c>
/// stops a broken config turning into a tight restart loop.
/// </summary>
public static class OtelService
{
    /// <summary>The agent's label.</summary>
    public const string Label = "dev.rtkelly.rig.otelcol";

    /// <summary>Seconds launchd waits before restarting a collector that exited.</summary>
    public const int ThrottleSeconds = 60;

    /// <summary>The plist's file name in the LaunchAgents folder.</summary>
    public const string PlistName = Label + ".plist";

    /// <summary>The agent running <paramref name="binary"/> with <paramref name="config"/>, both outputs to <paramref name="log"/>.</summary>
    public static byte[] Render(string binary, string config, string log) =>
        Plist.Render(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Label"] = Label,
            ["ProgramArguments"] = new[] { binary, "--config", config },
            ["RunAtLoad"] = true,
            ["KeepAlive"] = true,
            ["ThrottleInterval"] = ThrottleSeconds,
            ["ProcessType"] = "Background",
            ["LowPriorityIO"] = true,
            ["Nice"] = 10,
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

    /// <summary>Writes the agent for <paramref name="binary"/> and <paramref name="config"/> and loads it; returns the plist's path.</summary>
    /// <exception cref="Processes.ProcException"><c>launchctl bootstrap</c> failed.</exception>
    public static async Task<string> InstallAsync(Launchd launchd, OtelPaths paths, string binary, string config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launchd);
        ArgumentNullException.ThrowIfNull(paths);
        var path = PlistPath(launchd);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(paths.Log)!);
        await launchd.LoadAsync(path, Render(binary, config, paths.Log), cancellationToken).ConfigureAwait(false);
        return path;
    }

    /// <summary>Unloads and deletes the agent; false when it was not installed.</summary>
    public static Task<bool> UninstallAsync(Launchd launchd, CancellationToken cancellationToken = default) =>
        launchd is null ? throw new ArgumentNullException(nameof(launchd)) : launchd.RemoveAsync(PlistPath(launchd), cancellationToken);
}
