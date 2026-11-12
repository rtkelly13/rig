using System.Text.Json;
using Rig.Core.Processes;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// Chrome's macOS scripting dictionary through osascript: stable window and tab ids, titles and URLs. JXA runs in
/// osascript, never in a web page. Ported from <c>desktop_manager.chrome.drivers.macos_jxa</c>.
/// </summary>
/// <param name="proc">Runs osascript.</param>
public sealed class ChromeJxa(IProc proc)
{
    /// <summary>The osascript command a script is piped to.</summary>
    public static readonly IReadOnlyList<string> Osascript = ["/usr/bin/osascript", "-l", "JavaScript", "-"];

    /// <summary>How long one query may take before it counts as failed.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string Failed = "Chrome automation failed; check macOS Automation permission and retry";

    /// <summary>
    /// Runs <paramref name="script"/> and parses its JSON reply, still to be decoded. A failed, timed-out or non-JSON
    /// run throws <see cref="ChromeMacException"/>.
    /// </summary>
    public async Task<JsonElement> RunAsync(string script, CancellationToken cancellationToken = default)
    {
        var ran = await proc.RunAsync(new ProcRequest(Osascript) { StdinText = script, Timeout = Timeout }, cancellationToken).ConfigureAwait(false);
        if (!ran.Ok)
        {
            throw new ChromeMacException(Failed, new ProcException(Osascript, ran));
        }

        try
        {
            using var reply = JsonDocument.Parse(ran.Out);
            return reply.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ChromeMacException(Failed, ex);
        }
    }

    /// <summary>Chrome's windows and tabs now.</summary>
    public async Task<JxaSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        JxaDecode.Snapshot(await RunAsync(JxaScripts.Snapshot, cancellationToken).ConfigureAwait(false));

    /// <summary>The frontmost application's process, from AppKit; null when there is none.</summary>
    public async Task<int?> FrontmostPidAsync(CancellationToken cancellationToken = default) =>
        JxaDecode.FrontmostPid(await RunAsync(JxaScripts.Frontmost, cancellationToken).ConfigureAwait(false));

    /// <summary>Runs an action script and returns the status it replied with.</summary>
    public async Task<string> ActAsync(string script, CancellationToken cancellationToken = default) =>
        JxaDecode.Status(await RunAsync(script, cancellationToken).ConfigureAwait(false));
}

/// <summary>Chrome's profiles on macOS, from its <c>Local State</c> file.</summary>
public static class ChromeProfiles
{
    /// <summary><c>Local State</c>, relative to the home folder.</summary>
    public static readonly string LocalState = Path.Combine("Library", "Application Support", "Google", "Chrome", "Local State");

    /// <summary>
    /// Chrome's profiles by directory, from <c>Local State</c> under <paramref name="home"/>; empty when the file is
    /// missing, unreadable or not the shape Chrome writes. A profile entry that is not an object is skipped, and a
    /// name that is not a string reads as absent.
    /// </summary>
    public static IReadOnlyDictionary<string, ProfileInfo> Read(string home)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(home, LocalState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, ProfileInfo>();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return Parse(document.RootElement);
        }
        // A profile directory name with an unpaired surrogate escape cannot be read as a .NET key (InvalidOperationException).
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return new Dictionary<string, ProfileInfo>();
        }
    }

    private static Dictionary<string, ProfileInfo> Parse(JsonElement root)
    {
        var found = new Dictionary<string, ProfileInfo>(StringComparer.Ordinal);
        if (!JxaDecode.Object(root).TryGetProperty("profile", out var profile) || profile.ValueKind != JsonValueKind.Object)
        {
            return found;
        }

        if (!profile.TryGetProperty("info_cache", out var cache))
        {
            return found;
        }

        if (cache.ValueKind != JsonValueKind.Object)
        {
            // The section does not fit, so it reads as absent, as a lenient field does in Python.
            return found;
        }

        foreach (var entry in cache.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.Object)
            {
                found[entry.Name] = new ProfileInfo(Name(entry.Value, "name"), Name(entry.Value, "gaia_name"), Name(entry.Value, "gaia_given_name"), Name(entry.Value, "user_name"));
            }
        }

        return found;
    }

    private static string? Name(JsonElement info, string key) =>
        info.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? JxaDecode.Text(value) : null;
}
