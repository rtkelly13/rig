using System.Text.Json.Nodes;

namespace Rig.Core.Devenv;

/// <summary>
/// OpenCode's default user config, merged into <c>~/.config/opencode/config.json</c> as a union that never clobbers:
/// a key the user already set is kept, and nested objects merge key by key.
/// </summary>
public static class OpencodeDefaults
{
    /// <summary>The defaults: the config schema and the models the explore and general agents run.</summary>
    public static JsonObject Config => new()
    {
        ["$schema"] = "https://opencode.ai/config.json",
        ["agent"] = new JsonObject
        {
            ["explore"] = new JsonObject { ["model"] = "github-copilot/gemini-3-flash-preview" },
            ["general"] = new JsonObject { ["model"] = "github-copilot/claude-sonnet-4.6" },
        },
    };

    /// <summary><c>~/.config/opencode/config.json</c> under <paramref name="home"/>.</summary>
    public static string ConfigPath(string home) => Path.Join(home, ".config", "opencode", "config.json");

    /// <summary>
    /// <paramref name="existing"/> with <see cref="Config"/> merged in, or null when nothing was missing. The input is
    /// not changed.
    /// </summary>
    public static JsonObject? Merge(JsonObject existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var merged = (JsonObject)existing.DeepClone();
        return AddMissing(merged, Config) ? merged : null;
    }

    // Adds each default key target lacks, recursing where both sides hold an object; true when anything was added.
    private static bool AddMissing(JsonObject target, JsonObject defaults)
    {
        var added = false;
        foreach (var (key, value) in defaults)
        {
            if (!target.ContainsKey(key))
            {
                target[key] = value!.DeepClone();
                added = true;
            }
            else if (target[key] is JsonObject current && value is JsonObject nested)
            {
                added |= AddMissing(current, nested);
            }
        }

        return added;
    }
}
