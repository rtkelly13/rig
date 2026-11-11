using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rig.Core.Devenv;

/// <summary>How the installed gh scope overlay compares with the bundled one.</summary>
public enum OverlayState
{
    /// <summary>Installed and equal to the bundled overlay.</summary>
    InSync,

    /// <summary>Installed but different, or not readable JSON.</summary>
    OutOfDate,

    /// <summary>Not installed.</summary>
    Missing,
}

/// <summary>
/// The personal gh token scope overlay. The team baseline and the scope checks live in the workspace repo
/// (<c>devenv doctor git auth</c>); rig only places these personal extras at <c>~/.config/devenv/gh-scopes.json</c>,
/// where the workspace merges them in. <c>gh-scopes.json</c> is embedded here. Ported from devenv's <c>github_cli</c>.
/// </summary>
public static class GhScopes
{
    private static readonly Lazy<string> Bundled = new(() =>
    {
        using var stream = typeof(GhScopes).Assembly.GetManifestResourceStream("Rig.Core.Devenv.gh-scopes.json")
            ?? throw new InvalidOperationException("gh-scopes.json is not embedded in Rig.Core.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /// <summary>The bundled overlay's text, written verbatim on install.</summary>
    public static string Overlay => Bundled.Value;

    /// <summary>The scopes the overlay requires, in order.</summary>
    public static IReadOnlyList<string> Required => Section("required");

    /// <summary>Every scope the overlay names, in its <c>required</c>, <c>optional</c> and <c>disallowed</c> lists.</summary>
    public static IReadOnlyList<string> All => [.. Section("required"), .. Section("optional"), .. Section("disallowed")];

    /// <summary>
    /// Where the workspace devenv reads the overlay: <c>devenv/gh-scopes.json</c> under <paramref name="configHome"/>
    /// (<c>XDG_CONFIG_HOME</c>) when set, else under <c>~/.config</c>.
    /// </summary>
    public static string Target(string home, string? configHome) =>
        Path.Join(string.IsNullOrEmpty(configHome) ? Path.Join(home, ".config") : configHome, "devenv", "gh-scopes.json");

    /// <summary>The installed overlay at <paramref name="target"/> compared, as JSON, with the bundled one.</summary>
    public static OverlayState State(string target)
    {
        if (!File.Exists(target))
        {
            return OverlayState.Missing;
        }

        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(target)), JsonNode.Parse(Overlay)) ? OverlayState.InSync : OverlayState.OutOfDate;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return OverlayState.OutOfDate;
        }
    }

    /// <summary>How <c>devenv</c> describes <paramref name="state"/>.</summary>
    public static string Describe(this OverlayState state) => state switch
    {
        OverlayState.InSync => "in sync",
        OverlayState.OutOfDate => "out of date",
        _ => "not installed",
    };

    private static List<string> Section(string name) =>
        JsonNode.Parse(Overlay)![name] is JsonArray entries ? [.. entries.Select(e => (string)e!["scope"]!)] : [];
}
