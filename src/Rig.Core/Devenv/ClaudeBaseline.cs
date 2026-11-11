using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rig.Core.Policy;

namespace Rig.Core.Devenv;

/// <summary>Settings with the baseline merged in, and a line per addition.</summary>
/// <param name="Settings">The merged settings.</param>
/// <param name="Changes">One line per value added, as <c>+ key: value</c>.</param>
public sealed record SettingsMerge(JsonObject Settings, IReadOnlyList<string> Changes);

/// <summary>
/// The Claude Code settings baseline, merged into <c>~/.claude/settings.json</c> as a union that never clobbers: a key
/// already set, even to another value, is kept. Command permissions are not baselined here: they come from the estate
/// policy (<c>config/cli-allowlists.json</c>) through <see cref="NativeProjection"/>.
/// </summary>
public static class ClaudeBaseline
{
    /// <summary>Top-level preferences. <c>workflowSizeGuideline</c> is advice to Claude; <see cref="Env"/> enforces it.</summary>
    public static readonly IReadOnlyList<(string Key, JsonValue Value)> Preferences =
    [
        ("model", JsonValue.Create("opus-5.5")),
        ("effortLevel", JsonValue.Create("medium")),
        ("autoCompactEnabled", JsonValue.Create(true)),
        ("autoCompactWindow", JsonValue.Create(200000)),
        ("skipWorkflowUsageWarning", JsonValue.Create(true)),
        ("workflowSizeGuideline", JsonValue.Create("small")),
    ];

    /// <summary>
    /// Environment for every session. The caps keep one session to about three agents at once, since each agent's
    /// builds and tests compete for memory on an 8 GB machine.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, JsonValue Value)> Env =
    [
        ("CLAUDE_CODE_MAX_CONCURRENT_SUBAGENTS", JsonValue.Create("3")),
        ("CLAUDE_CODE_WORKFLOW_MAX_CONCURRENT_AGENTS", JsonValue.Create("3")),
    ];

    /// <summary>The permission preferences; <c>allow</c> and <c>deny</c> belong to the policy projection.</summary>
    public static readonly IReadOnlyList<(string Key, JsonValue Value)> Permissions =
    [
        ("defaultMode", JsonValue.Create("acceptEdits")),
    ];

    /// <summary>Clears the automated commit and PR footers while keeping custom text.</summary>
    public static readonly IReadOnlyList<(string Key, JsonValue Value)> Attribution =
    [
        ("commit", JsonValue.Create(string.Empty)),
        ("pr", JsonValue.Create(string.Empty)),
        ("sessionUrl", JsonValue.Create(false)),
    ];

    /// <summary>
    /// MCP servers that need no credentials and no per-machine paths. Servers needing an API key or a vault path are
    /// registered per machine with <c>claude mcp add</c>: a baseline is only for what is safe on every machine.
    /// </summary>
    public static IReadOnlyList<(string Name, JsonObject Config)> McpServers =>
    [
        ("chrome-devtools", new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("-y", "chrome-devtools-mcp@latest") }),
    ];

    /// <summary><c>~/.claude/settings.json</c> under <paramref name="home"/>.</summary>
    public static string SettingsPath(string home) => Path.Join(home, ".claude", "settings.json");

    /// <summary>
    /// <paramref name="existing"/> with the baseline and the session-ledger hook (<paramref name="sessionEndCommand"/>)
    /// merged in. A wrong-typed <c>permissions</c>, <c>env</c>, <c>attribution</c> or <c>mcpServers</c> is replaced by
    /// an object; an existing MCP server is never modified, even when it differs from the baseline (ADR 0015: clobbering
    /// a hand-configured server destroys credentials that cannot be regenerated here).
    /// </summary>
    public static SettingsMerge Merge(JsonObject existing, string sessionEndCommand)
    {
        ArgumentNullException.ThrowIfNull(existing);
        var merged = (JsonObject)existing.DeepClone();
        var changes = new List<string>();

        AddMissing(ObjectAt(merged, "permissions"), Permissions, "permissions.", changes);
        AddMissing(merged, Preferences, string.Empty, changes);
        AddMissing(ObjectAt(merged, "env"), Env, "env.", changes);
        AddMissing(ObjectAt(merged, "attribution"), Attribution, "attribution.", changes);

        var servers = ObjectAt(merged, "mcpServers");
        foreach (var (name, config) in McpServers.Where(s => !servers.ContainsKey(s.Name)))
        {
            servers[name] = config;
            changes.Add($"+ mcpServers.{name}");
        }

        changes.AddRange(SessionLedger.Merge(merged, sessionEndCommand));
        return new SettingsMerge(merged, changes);
    }

    /// <summary>Python's <c>repr</c> of a baseline value, as the change lines show it: <c>'text'</c>, <c>True</c>, <c>3</c>.</summary>
    public static string Repr(JsonValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.GetValueKind() switch
        {
            JsonValueKind.String => Quote(value.GetValue<string>()),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => value.ToJsonString(),
        };
    }

    // Python's str repr: single quotes, or double quotes when the text holds a ' and no ".
    private static string Quote(string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal);
        return text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal)
            ? $"\"{escaped}\""
            : $"'{escaped.Replace("'", "\\'", StringComparison.Ordinal)}'";
    }

    // The object at key, replacing a missing or wrong-typed value with an empty one in place.
    private static JsonObject ObjectAt(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject found)
        {
            return found;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static void AddMissing(JsonObject target, IEnumerable<(string Key, JsonValue Value)> baseline, string prefix, List<string> changes)
    {
        foreach (var (key, value) in baseline.Where(b => !target.ContainsKey(b.Key)))
        {
            target[key] = value.DeepClone();
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"+ {prefix}{key}: {Repr(value)}"));
        }
    }
}
