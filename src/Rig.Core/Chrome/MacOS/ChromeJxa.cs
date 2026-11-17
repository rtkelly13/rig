using System.Text.Json;
using Rig.Core.Processes;

namespace Rig.Core.Chrome.MacOS;

/// <summary>
/// Chrome's macOS scripting dictionary through osascript: stable window and tab ids, titles and URLs. JXA runs in
/// osascript, never in a web page.
/// </summary>
/// <param name="proc">Runs osascript.</param>
public sealed class ChromeJxa(IProc proc) : IChromeJxa
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
    public JsonElement Run(string script)
    {
        var ran = proc.Run(new ProcRequest(Osascript) { StdinText = script, Timeout = Timeout });
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

    /// <inheritdoc/>
    public JxaSnapshot Snapshot() => JxaDecode.Snapshot(Run(JxaScripts.Snapshot));

    /// <inheritdoc/>
    public int? FrontmostPid() => JxaDecode.FrontmostPid(Run(JxaScripts.Frontmost));

    /// <inheritdoc/>
    public string Act(string script) => JxaDecode.Status(Run(script));
}

/// <summary>Chrome's scripting dictionary, as the macOS driver uses it. Every call throws <see cref="ChromeMacException"/> on failure.</summary>
public interface IChromeJxa
{
    /// <summary>Chrome's windows and tabs now.</summary>
    JxaSnapshot Snapshot();

    /// <summary>The frontmost application's process, from AppKit; null when there is none.</summary>
    int? FrontmostPid();

    /// <summary>Runs an action script and returns the status it replied with.</summary>
    string Act(string script);
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
            // The section does not fit, so it reads as absent.
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
