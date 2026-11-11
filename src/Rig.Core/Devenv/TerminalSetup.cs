using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Devenv;

/// <summary>Windows Terminal's settings as doctor reports them.</summary>
/// <param name="Path">The settings file, or null when Windows Terminal is not installed.</param>
/// <param name="SnapToGrid">Its <c>snapToGridOnResize</c>, or null when unset or unreadable.</param>
public sealed record TerminalStatus(string? Path, bool? SnapToGrid);

/// <summary>
/// devenv's terminal setup: Windows Terminal's defaults and the Oh My Posh prompt hook. Ported from devenv's
/// <c>steps/terminal.py</c>.
/// </summary>
public static class TerminalSetup
{
    /// <summary>The font setup gives Windows Terminal.</summary>
    public const string Font = "JetBrainsMono Nerd Font";

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Windows Terminal's settings file under <paramref name="localAppData"/>: the stable, preview, then unpackaged
    /// install's existing file, else the first whose folder exists, else null.
    /// </summary>
    public static string? SettingsPath(string localAppData)
    {
        string[] candidates =
        [
            Path.Join(localAppData, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Join(localAppData, "Packages", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe", "LocalState", "settings.json"),
            Path.Join(localAppData, "Microsoft", "Windows Terminal", "settings.json"),
        ];
        return candidates.FirstOrDefault(File.Exists) ?? candidates.FirstOrDefault(c => Directory.Exists(Path.GetDirectoryName(c)));
    }

    /// <summary>The settings at <paramref name="path"/>, comments and trailing commas allowed; null when it is not a JSON object.</summary>
    public static JsonObject? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) as JsonObject : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What doctor reports about the settings at <paramref name="path"/>.</summary>
    public static TerminalStatus Status(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new TerminalStatus(null, null);
        }

        return new TerminalStatus(path, Load(path)?["snapToGridOnResize"] is JsonValue value && value.TryGetValue<bool>(out var snap) ? snap : null);
    }

    /// <summary>
    /// Turns off <c>snapToGridOnResize</c> (it jitters while CLI tools render) and sets the default profile's font
    /// (size 10 unless set) and starting folder, in place. Returns each change made; none when it was already so.
    /// </summary>
    public static IReadOnlyList<string> Apply(JsonObject settings, string font, string startingDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var changes = new List<string>();
        if (settings["snapToGridOnResize"] is not JsonValue snap || !snap.TryGetValue<bool>(out var on) || on)
        {
            settings["snapToGridOnResize"] = false;
            changes.Add("Disabled snapToGridOnResize (prevents snap break and margin jitter)");
        }

        var defaults = Child(Child(settings, "profiles"), "defaults");
        var fontSettings = Child(defaults, "font");
        if (Text(fontSettings, "face") != font)
        {
            fontSettings["face"] = font;
            changes.Add($"Set font face to {font}");
        }

        if (fontSettings["size"] is null)
        {
            fontSettings["size"] = 10;
            changes.Add("Set font size to 10");
        }

        if (Text(defaults, "startingDirectory") != startingDirectory)
        {
            defaults["startingDirectory"] = startingDirectory;
            changes.Add($"Set starting directory to {startingDirectory}");
        }

        return changes;
    }

    /// <summary>The line that starts Oh My Posh in <paramref name="profile"/>'s shell.</summary>
    public static string PromptHook(string profile, bool windows) =>
        windows ? "oh-my-posh init pwsh | Invoke-Expression"
        : Path.GetFileName(profile) == ".zshrc" ? "eval \"$(oh-my-posh init zsh)\""
        : "eval \"$(oh-my-posh init bash)\"";

    // The object under key, replacing anything that is not one so the settings can be written.
    private static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject child)
        {
            return child;
        }

        child = [];
        parent[key] = child;
        return child;
    }

    private static string? Text(JsonObject parent, string key) => parent[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
