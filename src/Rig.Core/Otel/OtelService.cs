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
}
