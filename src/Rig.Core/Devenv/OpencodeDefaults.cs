using System.Text.Json.Nodes;

namespace Rig.Core.Devenv;

/// <summary>
/// OpenCode's default user config, merged into the config file OpenCode reads as a union that never clobbers:
/// a key the user already set is kept, and nested objects merge key by key.
/// </summary>
public static class OpencodeDefaults
{
    /// <summary>The defaults: the config schema and the models the explore and general agents run.</summary>
    public static JsonObject Config => new()
    {
        ["$schema"] = "https://opencode.ai/config.json",
        ["agents"] = new JsonObject
        {
            ["explore"] = new JsonObject { ["model"] = "github-copilot/gemini-3-flash-preview" },
            ["general"] = new JsonObject { ["model"] = "github-copilot/claude-sonnet-4.6" },
        },
    };

    /// <summary>
    /// The config file OpenCode reads under <paramref name="home"/>: an existing <c>opencode.json</c>, then
    /// <c>opencode.jsonc</c>, then the v1 <c>config.json</c>, falling back to <c>opencode.json</c> when there is none.
    /// </summary>
    public static string ConfigPath(string home)
    {
        var folder = Path.Join(home, ".config", "opencode");
        foreach (var name in new[] { "opencode.json", "opencode.jsonc", "config.json" })
        {
            var candidate = Path.Join(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Join(folder, "opencode.json");
    }

    /// <summary>
    /// <paramref name="existing"/> with <see cref="Config"/> merged in, or null when nothing was missing. The input is
    /// not changed.
    /// </summary>
    public static JsonObject? Merge(JsonObject existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var merged = (JsonObject)existing.DeepClone();
        return AddMissing(merged, DefaultsFor(existing)) ? merged : null;
    }

    // OpenCode migrates legacy `agent` and `mode` entries into native agents, then lets the native entry win.
    // Adding a default for a name a legacy section already defines would replace the user's entry, so leave it out.
    private static JsonObject DefaultsFor(JsonObject existing)
    {
        var defaults = Config;
        if (defaults["agents"] is not JsonObject agents)
        {
            return defaults;
        }

        foreach (var legacyKey in new[] { "agent", "mode" })
        {
            if (existing[legacyKey] is not JsonObject legacy)
            {
                continue;
            }

            foreach (var (name, _) in legacy)
            {
                _ = agents.Remove(name);
            }
        }

        return defaults;
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
