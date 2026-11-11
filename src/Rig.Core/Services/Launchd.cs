using Rig.Core.Processes;

namespace Rig.Core.Services;

/// <summary>
/// The few <c>launchctl</c> operations rig's LaunchAgents need (the OTel collector and the vault's knowledge server), in one
/// launchd domain. <c>bootstrap</c> and <c>bootout</c> are the current verbs; <c>load</c> and <c>unload</c> are deprecated.
/// </summary>
/// <param name="proc">Runs <c>launchctl</c>.</param>
/// <param name="agentsDir">Where agents' plists live, <c>~/Library/LaunchAgents</c>.</param>
/// <param name="domain">The launchd domain, <c>gui/&lt;uid&gt;</c> for the logged-in user; see <see cref="GuiDomain"/>.</param>
public sealed class Launchd(IProc proc, string agentsDir, string domain)
{
    /// <summary>Where agents' plists live.</summary>
    public string AgentsDir => agentsDir;

    /// <summary>The GUI domain of the user with <paramref name="uid"/>.</summary>
    public static string GuiDomain(uint uid) => $"gui/{uid}";

    /// <summary>The agents folder under <paramref name="home"/>.</summary>
    public static string AgentsDirUnder(string home) => Path.Join(home, "Library", "LaunchAgents");

    /// <summary>Writes <paramref name="plist"/> to <paramref name="path"/> and (re)loads it, replacing any earlier copy.</summary>
    /// <exception cref="ProcException"><c>launchctl bootstrap</c> failed.</exception>
    public async Task LoadAsync(string path, byte[] plist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(plist);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await UnloadAsync(path, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(path, plist, cancellationToken).ConfigureAwait(false);
        await RunCheckedAsync(["launchctl", "bootstrap", domain, path], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops and unloads the agent at <paramref name="path"/>, if it is loaded. The file stays.</summary>
    public async Task UnloadAsync(string path, CancellationToken cancellationToken = default) =>
        _ = await proc.RunAsync(new ProcRequest(["launchctl", "bootout", domain, path]), cancellationToken).ConfigureAwait(false);

    /// <summary>Unloads and deletes the agent at <paramref name="path"/>; false when it was not installed.</summary>
    public async Task<bool> RemoveAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await UnloadAsync(path, cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        return true;
    }

    /// <summary>Whether launchd has the agent <paramref name="label"/> loaded.</summary>
    public async Task<bool> LoadedAsync(string label, CancellationToken cancellationToken = default) =>
        (await proc.RunAsync(new ProcRequest(["launchctl", "print", $"{domain}/{label}"]), cancellationToken).ConfigureAwait(false)).Ok;

    /// <summary>Starts the agent now; with <paramref name="restart"/>, kills a running instance first.</summary>
    /// <exception cref="ProcException"><c>launchctl kickstart</c> failed.</exception>
    public Task KickstartAsync(string label, bool restart = false, CancellationToken cancellationToken = default) =>
        RunCheckedAsync(["launchctl", "kickstart", .. restart ? ["-k"] : Array.Empty<string>(), $"{domain}/{label}"], cancellationToken);

    private async Task RunCheckedAsync(IReadOnlyList<string> argv, CancellationToken cancellationToken) =>
        (await proc.RunAsync(new ProcRequest(argv), cancellationToken).ConfigureAwait(false)).EnsureOk(argv);
}
