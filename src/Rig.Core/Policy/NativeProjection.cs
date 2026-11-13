using System.Text.Json;
using System.Text.Json.Nodes;
using IOPath = System.IO.Path;

namespace Rig.Core.Policy;

/// <summary>The literal tokens a policy glob starts with, and whether anything may follow them.</summary>
/// <param name="Tokens">The command's words.</param>
/// <param name="Wildcard">Whether the glob ended in <c>*</c>.</param>
public sealed record CommandPrefix(IReadOnlyList<string> Tokens, bool Wildcard)
{
    /// <summary>The words joined by single spaces.</summary>
    public string Command => string.Join(' ', Tokens);
}

/// <summary>
/// Writes the estate command policy into one agent's native permission file, keeping every setting the policy does
/// not own. Ported from <c>rig.policy.projections</c>.
/// </summary>
/// <param name="System">The agent surface: <c>claude</c>, <c>codex</c>, <c>opencode</c> or <c>antigravity</c>.</param>
/// <param name="Path">Its native permission file, or null when it exposes none.</param>
/// <param name="Windows">Whether files are written with CRLF line endings, as Python's text mode did there.</param>
public sealed record NativeProjection(string System, string? Path, bool Windows)
{
    /// <summary>Every surface, in the order <c>policy sync</c> visits them.</summary>
    public static readonly IReadOnlyList<string> Systems = ["opencode", "claude", "codex", "antigravity"];

    private const string BeginMarker = "# BEGIN shared-utilities policy";
    private const string EndMarker = "# END shared-utilities policy";

    // Surfaces whose rules always match as prefixes: an exact policy command still allows anything after it.
    private static readonly HashSet<string> PrefixMatchOnly = new(["codex", "antigravity"], StringComparer.Ordinal);

    /// <summary>The projection for <paramref name="system"/> under <paramref name="home"/>; only opencode's looks at disk.</summary>
    public static NativeProjection For(string system, string home, bool windows)
    {
        ArgumentNullException.ThrowIfNull(system);
        var path = system switch
        {
            "claude" => IOPath.Join(home, ".claude", "settings.json"),
            "codex" => IOPath.Join(home, ".codex", "rules", "default.rules"),
            "opencode" => OpencodeConfig(IOPath.Join(home, ".config", "opencode")),
            "antigravity" => IOPath.Join(home, ".gemini", "antigravity-cli", "settings.json"),
            _ => throw new ArgumentException($"Unknown native permission system: {system}", nameof(system)),
        };
        return new NativeProjection(system, path, windows);
    }

    /// <summary>Applies <paramref name="policy"/> to each of <paramref name="systems"/> under <paramref name="home"/>.</summary>
    public static IReadOnlyList<ProjectionResult> ApplyAll(CommandPolicy policy, IEnumerable<string> systems, string home, bool dryRun, bool windows) =>
        [.. systems.Select(s => For(s, home, windows).Apply(policy, dryRun))];

    /// <summary>Writes <paramref name="policy"/> into the native file, or says what it would do when <paramref name="dryRun"/>.</summary>
    public ProjectionResult Apply(CommandPolicy policy, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (Path is null)
        {
            return new ProjectionResult(System, null, ProjectionStatus.Unsupported, "No persistent command permission file is exposed by this CLI.");
        }

        try
        {
            if (System == "codex")
            {
                return ApplyCodex(Path, policy, dryRun);
            }

            var data = LoadObject(Path);
            var rendered = Render(data, policy);
            if (JsonNode.DeepEquals(rendered, data))
            {
                return new ProjectionResult(System, Path, ProjectionStatus.Unchanged, "Already up to date.");
            }

            if (dryRun)
            {
                return new ProjectionResult(System, Path, ProjectionStatus.DryRun, "Would update native rules.");
            }

            WriteText(Path, PyJson.Dumps(rendered) + "\n");
            return new ProjectionResult(System, Path, ProjectionStatus.Applied, "Updated native rules.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new ProjectionResult(System, Path, ProjectionStatus.Failed, e.Message);
        }
    }

    /// <summary>
    /// <paramref name="pattern"/> as a native command prefix: its words, and whether it ended in <c>*</c>. Null when
    /// it has a <c>*</c> or <c>?</c> anywhere else, or no words, since a prefix cannot express it.
    /// </summary>
    public static CommandPrefix? Prefix(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var trimmed = pattern.TrimEnd();
        var wildcard = trimmed.EndsWith('*');
        var text = wildcard ? trimmed[..^1].TrimEnd() : pattern.Trim();
        if (text.IndexOfAny(['*', '?']) >= 0)
        {
            return null;
        }

        try
        {
            var tokens = Shlex.Split(text);
            return tokens.Count > 0 ? new CommandPrefix(tokens, wildcard) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="pattern"/> as a Claude Code <c>Bash(...)</c> rule. A literal or trailing-<c>*</c> glob takes
    /// the prefix form every surface shares; a glob with an interior <c>*</c> has none, so the prefix-only surfaces
    /// skip it, but Claude Code matches <c>*</c> anywhere, so it goes there verbatim.
    /// </summary>
    public static string? ClaudeRule(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (Prefix(pattern) is { } prefix)
        {
            return prefix.Wildcard ? $"Bash({prefix.Command}:*)" : $"Bash({prefix.Command})";
        }

        var text = string.Join(' ', pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length == 0 || text.IndexOfAny(['?', '"', '\'']) >= 0 ? null : $"Bash({text})";
    }

    private static string OpencodeConfig(string folder)
    {
        foreach (var name in new[] { "opencode.json", "opencode.jsonc", "config.json" })
        {
            var candidate = IOPath.Join(folder, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return IOPath.Join(folder, "opencode.json");
    }

    // Python read files in text mode: universal newlines.
    private static string ReadText(string path) => File.ReadAllText(path).ReplaceLineEndings("\n");

    private static JsonObject LoadObject(string path) =>
        !File.Exists(path) ? [] : JsonNode.Parse(ReadText(path)) as JsonObject ?? throw new InvalidDataException("native config root must be an object");

    private static JsonObject ObjectAt(JsonObject parent, string key, string label)
    {
        var child = parent[key] switch
        {
            null => [],
            JsonObject existing => existing,
            _ => throw new InvalidDataException($"{label} must be an object"),
        };
        parent[key] = child;
        return child;
    }

    private static JsonArray ArrayAt(JsonObject parent, string key, string label)
    {
        var child = parent[key] switch
        {
            null => [],
            JsonArray existing => existing,
            _ => throw new InvalidDataException($"{label} must be an array"),
        };
        parent[key] = child;
        return child;
    }

    private static string? AsText(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static void RemoveAll(JsonArray target, IEnumerable<string> values)
    {
        var unwanted = values.ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (AsText(target[i]) is { } text && unwanted.Contains(text))
            {
                target.RemoveAt(i);
            }
        }
    }

    private static void MergeUnique(JsonArray target, IEnumerable<string> values)
    {
        var existing = target.Select(AsText).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (existing.Add(value))
            {
                target.Add(value);
            }
        }
    }

    // Antigravity deprecated `unsandboxed(...)` and reports each such rule as invalid; rewrite them as `command(...)`,
    // keeping each rule once so a second sync cannot grow the list.
    private static void MigrateAntigravity(JsonArray rules)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var migrated = new List<JsonNode?>();
        foreach (var rule in rules)
        {
            if (AsText(rule) is not { } text)
            {
                migrated.Add(rule?.DeepClone());
                continue;
            }

            if (text.StartsWith("unsandboxed(", StringComparison.Ordinal) && text.EndsWith(')'))
            {
                text = $"command({text["unsandboxed(".Length..^1]})";
            }

            if (seen.Add(text))
            {
                migrated.Add(text);
            }
        }

        rules.Clear();
        foreach (var rule in migrated)
        {
            rules.Add(rule);
        }
    }

    private static IEnumerable<string> ClaudeRules(CommandPolicy policy, CommandRisk risk, bool excluded) =>
        policy.CommandRules
            .Where(r => r.Risk == risk && r.ExcludeSystems.Contains("claude") == excluded)
            .Select(r => ClaudeRule(r.Pattern))
            .OfType<string>();

    private static string OpencodeRule(CommandPrefix prefix) => prefix.Wildcard ? $"{prefix.Command} *" : prefix.Command;

    private static IEnumerable<string> AntigravityRules(IEnumerable<CommandPrefix> prefixes) => prefixes.Select(p => $"command({p.Command})");

    private static string CodexRule(CommandPrefix prefix, string decision, string rationale) =>
        $"prefix_rule(\n    pattern=[{string.Join(", ", prefix.Tokens.Select(t => PyJson.Dumps(t)))}],\n    decision='{decision}',\n    justification='{rationale}',\n)";

    private void WriteText(string path, string text)
    {
        _ = Directory.CreateDirectory(IOPath.GetDirectoryName(IOPath.GetFullPath(path))!);
        File.WriteAllText(path, Windows ? text.Replace("\n", "\r\n", StringComparison.Ordinal) : text);
    }

    private JsonObject Render(JsonObject data, CommandPolicy policy)
    {
        var rendered = (JsonObject)data.DeepClone();
        if (System is "claude" or "antigravity")
        {
            var permissions = ObjectAt(rendered, "permissions", "permissions");
            var allow = ArrayAt(permissions, "allow", "permissions.allow");
            var deny = ArrayAt(permissions, "deny", "permissions.deny");
            if (System == "antigravity")
            {
                MigrateAntigravity(allow);
                MigrateAntigravity(deny);
            }

            foreach (var (target, risk) in new[] { (allow, CommandRisk.Safe), (deny, CommandRisk.Denied) })
            {
                if (System == "claude")
                {
                    RemoveAll(target, ClaudeRules(policy, risk, excluded: true));
                    MergeUnique(target, ClaudeRules(policy, risk, excluded: false));
                }
                else
                {
                    RemoveAll(target, AntigravityRules(Excluded(policy, risk)));
                    MergeUnique(target, AntigravityRules(RulesFor(policy, risk)));
                }
            }

            return rendered;
        }

        if (System != "opencode")
        {
            throw new ArgumentException($"JSON projection is not supported for {System}");
        }

        var permission = ObjectAt(rendered, "permission", "permission");
        // OpenCode v2 names its shell tool "shell" and v1 "bash"; both are written so every version resolves.
        var shells = new[] { ObjectAt(permission, "shell", "permission.shell"), ObjectAt(permission, "bash", "permission.bash") };
        foreach (var shell in shells)
        {
            _ = shell.TryAdd("*", "ask");
        }

        foreach (var (risk, effect) in new[] { (CommandRisk.Safe, "allow"), (CommandRisk.Local, "ask"), (CommandRisk.External, "ask"), (CommandRisk.Denied, "deny") })
        {
            foreach (var rule in Excluded(policy, risk).Select(OpencodeRule))
            {
                foreach (var shell in shells.Where(s => AsText(s[rule]) == effect))
                {
                    _ = shell.Remove(rule);
                }
            }

            foreach (var rule in RulesFor(policy, risk).Select(OpencodeRule))
            {
                foreach (var shell in shells)
                {
                    _ = shell.TryAdd(rule, effect);
                }
            }
        }

        MirrorShellRules(shells);

        // Sibling repos across the estate root stay reachable without tripping OpenCode's external_directory guard.
        _ = ObjectAt(permission, "external_directory", "permission.external_directory").TryAdd("~/code/**", "allow");
        return rendered;
    }

    // OpenCode v2 merges the two maps into one ordered rule list and applies the last match, so a rule present in only
    // one map loses to the other map's "*" fallback. Copy each missing key into the other map; the copies follow the
    // fallback that would otherwise hide them.
    private static void MirrorShellRules(IReadOnlyList<JsonObject> shells)
    {
        var union = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var shell in shells)
        {
            foreach (var (rule, effect) in shell)
            {
                _ = union.TryAdd(rule, effect?.DeepClone());
            }
        }

        foreach (var shell in shells)
        {
            foreach (var (rule, effect) in union)
            {
                _ = shell.TryAdd(rule, effect?.DeepClone());
            }
        }
    }

    private List<CommandPrefix> RulesFor(CommandPolicy policy, CommandRisk risk)
    {
        var shadows = risk == CommandRisk.Safe ? UnexpressibleDenies(policy) : [];
        return
        [
            .. policy.CommandRules
                .Where(r => r.Risk == risk && !r.ExcludeSystems.Contains(System))
                .Select(r => Prefix(r.Pattern))
                .OfType<CommandPrefix>()
                .Where(p => !Shadowed(p, shadows)),
        ];
    }

    // Rules kept off this surface, so a copy an earlier sync wrote is removed (merges only add): those opted out with
    // exclude_systems and, for allow rules, any a deny this surface cannot express would carve an exception out of.
    private List<CommandPrefix> Excluded(CommandPolicy policy, CommandRisk risk)
    {
        var shadows = risk == CommandRisk.Safe ? UnexpressibleDenies(policy) : [];
        return
        [
            .. policy.CommandRules
                .Where(r => r.Risk == risk)
                .Select(r => (Rule: r, Prefix: Prefix(r.Pattern)))
                .Where(x => x.Prefix is not null && (x.Rule.ExcludeSystems.Contains(System) || Shadowed(x.Prefix, shadows)))
                .Select(x => x.Prefix!),
        ];
    }

    // Deny globs that apply here but have no prefix form (an interior `*`): prefix-only surfaces drop them.
    private List<string> UnexpressibleDenies(CommandPolicy policy) =>
    [
        .. policy.CommandRules
            .Where(r => r.Risk == CommandRisk.Denied && !r.ExcludeSystems.Contains(System) && Prefix(r.Pattern) is null)
            .Select(r => r.Pattern),
    ];

    // Whether allowing `prefix` would also allow a command one of `denies` forbids. A surface that cannot carry those
    // denies would turn each into an allow (`gh stack sync *` without `gh stack sync *--prune*`), so such an allow is held
    // back and the surface keeps prompting. An exact command is checked against each glob; a wildcard prefix overlaps any
    // glob whose literal head is compatible with it. On Codex and Antigravity every rule is a prefix, so an exact
    // command counts as a wildcard there.
    private bool Shadowed(CommandPrefix prefix, List<string> denies)
    {
        var wildcard = prefix.Wildcard || PrefixMatchOnly.Contains(System);
        foreach (var deny in denies)
        {
            if (!wildcard)
            {
                if (Glob.Matches(prefix.Command, deny, Windows))
                {
                    return true;
                }

                continue;
            }

            var head = deny.Split('*', 2)[0];
            var words = head.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var headTokens = head.EndsWith(' ') ? words : words[..^Math.Min(1, words.Length)];
            var shorter = Math.Min(headTokens.Length, prefix.Tokens.Count);
            if (prefix.Tokens.Take(shorter).SequenceEqual(headTokens.Take(shorter), StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private ProjectionResult ApplyCodex(string path, CommandPolicy policy, bool dryRun)
    {
        var existing = File.Exists(path) ? ReadText(path) : string.Empty;
        var blocks = Enum.GetValues<CommandRisk>().SelectMany(risk => RulesFor(policy, risk).Select(p => CodexRule(p, Decision(risk), Rationale(risk))));
        var managed = $"{BeginMarker}\n{string.Join('\n', blocks)}\n{EndMarker}";
        string updated;
        if (existing.Contains(BeginMarker, StringComparison.Ordinal) && existing.Contains(EndMarker, StringComparison.Ordinal))
        {
            var begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
            var remainder = existing[(begin + BeginMarker.Length)..];
            var end = remainder.IndexOf(EndMarker, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException($"{EndMarker} comes before {BeginMarker}");
            }

            // A file holding only the block keeps it at the top, so a second sync finds nothing to change.
            var before = existing[..begin].TrimEnd();
            updated = $"{(before.Length > 0 ? before + "\n\n" : string.Empty)}{managed}\n{remainder[(end + EndMarker.Length)..].TrimStart()}";
        }
        else
        {
            updated = existing.Trim().Length > 0 ? $"{existing.TrimEnd()}\n\n{managed}\n" : $"{managed}\n";
        }

        if (updated == existing)
        {
            return new ProjectionResult("codex", path, ProjectionStatus.Unchanged, "Already up to date.");
        }

        if (dryRun)
        {
            return new ProjectionResult("codex", path, ProjectionStatus.DryRun, "Would update native rules.");
        }

        WriteText(path, updated);
        return new ProjectionResult("codex", path, ProjectionStatus.Applied, "Updated managed native rules block.");
    }

    private static string Decision(CommandRisk risk) => risk switch
    {
        CommandRisk.Safe => "allow",
        CommandRisk.Denied => "forbidden",
        _ => "prompt",
    };

    private static string Rationale(CommandRisk risk) => risk switch
    {
        CommandRisk.Safe => "Estate validation command.",
        CommandRisk.Local => "Local mutation requires approval.",
        CommandRisk.External => "External side effect requires approval.",
        _ => "Blocked by the estate policy.",
    };
}
