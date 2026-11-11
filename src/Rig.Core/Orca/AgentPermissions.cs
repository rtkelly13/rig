namespace Rig.Core.Orca;

/// <summary>One agent setting as Orca has it against what the policy wants.</summary>
/// <param name="Subject">What it is about: <c>settings</c> or <c>agent &lt;name&gt;</c>.</param>
/// <param name="Check">Which setting.</param>
/// <param name="Have">What Orca has.</param>
/// <param name="Want">What the policy wants.</param>
/// <param name="Fix">How to make them agree.</param>
public sealed record AgentCheck(string Subject, string Check, string Have, string Want, string Fix)
{
    /// <summary>Whether Orca has what the policy wants.</summary>
    public bool Ok => Have == Want;
}

/// <summary>
/// Orca's agent permission modes (Settings → Agents), as checks and as launch lines. Since Orca 1.4.255 an agent's
/// launch command is its mode's flags plus its custom arguments; <c>settings.update</c> takes a whole launch line in
/// <c>agentDefaultArgs</c> and splits it again, so sync sends each agent's composed line. See <c>docs/orca.md</c>.
/// </summary>
public static class AgentPermissions
{
    /// <summary>The permission modes Orca offers.</summary>
    public static readonly IReadOnlyCollection<string> Modes = ["own", "ask", "accept-edits", "auto", "full-access"];

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["own"] = "Own",
        ["ask"] = "Ask",
        ["accept-edits"] = "Accept edits",
        ["auto"] = "Auto",
        ["full-access"] = "Full access",
    };

    private static readonly string[] ClaudeFamily = ["claude", "claude-agent-teams"];

    // The flags each mode adds, from Orca 1.4.255's ARGS_BY_CHOICE, AUTO_TUI_AGENT_ARGS and YOLO_TUI_AGENT_ARGS. Only
    // agents rig has a policy for are listed; a mode an agent is missing from is not checked at launch.
    private static readonly Dictionary<string, Dictionary<string, string>> Flags = new(StringComparer.Ordinal)
    {
        ["ask"] = With(ClaudeFamily, "--permission-mode default", ("codex", "-a on-request -s read-only -c approvals_reviewer=user")),
        ["accept-edits"] = With(ClaudeFamily, "--permission-mode acceptEdits"),
        ["auto"] = With(ClaudeFamily, "--permission-mode auto", ("codex", "--approve-for-me")),
        ["full-access"] = With(
            [.. ClaudeFamily, "openclaude", "antigravity"],
            "--dangerously-skip-permissions",
            ("codex", "--dangerously-bypass-approvals-and-sandbox"),
            ("copilot", "--yolo"),
            ("gemini", "--yolo")),
    };

    // Agents with a permission choice in Orca 1.4.255 (hasConfigurableTuiAgentPermissions). Orca keeps an override for
    // others but launches them the same in every mode, so it is not checked.
    private static readonly HashSet<string> Configurable = new(StringComparer.Ordinal)
    {
        "claude", "codebuddy", "claude-agent-teams", "openclaude", "codex", "qoder", "qoder-cn", "gemini",
        "antigravity", "aider", "amp", "kiro", "crush", "autohand", "cline", "command-code", "continue",
        "cursor", "kimi", "muse", "zcode", "mistral-vibe", "qwen-code", "rovo", "hermes", "copilot", "grok",
        "devin", "ante", "trae", "droid", "goose",
    };

    /// <summary>Whether Orca offers <paramref name="agent"/> a permission mode at all.</summary>
    public static bool IsConfigurable(string agent) => Configurable.Contains(agent);

    /// <summary>The flags Orca adds to <paramref name="agent"/>'s launch line in <paramref name="mode"/>, or null when rig does not know them.</summary>
    public static string? FlagsFor(string mode, string agent) =>
        mode == "own" ? "" : Flags[mode].GetValueOrDefault(agent);

    /// <summary>The label Orca's Settings show for <paramref name="mode"/>.</summary>
    public static string Label(string mode) => Labels[mode];

    /// <summary>Each policy agent's custom arguments as Orca stores them (<c>agentDefaultArgs</c>), against the policy.</summary>
    public static IReadOnlyList<AgentCheck> CustomArgsChecks(IReadOnlyDictionary<string, string> want, IReadOnlyDictionary<string, string> stored) =>
        [.. want.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new AgentCheck(
            $"agent {pair.Key}",
            "custom args",
            stored.GetValueOrDefault(pair.Key, "").Trim(),
            pair.Value.Trim(),
            $"Orca Settings → Agents → {pair.Key}: set custom arguments to \"{pair.Value.Trim()}\""))];

    /// <summary>
    /// The default mode, and the mode of each policy agent and each of <paramref name="agents"/>, as Orca stores them. Pass
    /// Orca's override keys as <paramref name="agents"/> so an override the policy does not expect is caught; agents Orca
    /// offers no mode for are left out.
    /// </summary>
    public static IReadOnlyList<AgentCheck> PermissionChecks(
        AgentPermissionsPolicy policy, IEnumerable<string> agents, string? storedDefault, IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(agents);
        var fallback = storedDefault ?? "own";
        List<AgentCheck> checks =
        [
            new("settings", "permission default", fallback, policy.Default, $"Orca Settings → Agents: set the default permission to {Label(policy.Default)}"),
        ];
        foreach (var agent in agents.Concat(policy.Agents.Keys).Where(IsConfigurable).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var want = policy.Wanted(agent);
            var have = overrides?.GetValueOrDefault(agent) ?? fallback;
            checks.Add(new($"agent {agent}", "permission", have, want, $"Orca Settings → Agents → {agent}: set permission to {Label(want)}"));
        }

        return checks;
    }

    /// <summary>
    /// What the running Orca launches each policy agent with, against its mode's flags plus its custom arguments, compared
    /// as sorted token sets since the order Orca joins the two in is not part of the policy.
    /// </summary>
    public static IReadOnlyList<AgentCheck> LaunchChecks(
        AgentPermissionsPolicy policy, IReadOnlyDictionary<string, string> customArgs, IReadOnlyDictionary<string, string> launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return [.. WantedLines(policy, customArgs).OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new AgentCheck(
            $"agent {pair.Key}",
            "launch args",
            Tokens(launch.GetValueOrDefault(pair.Key, "")),
            Tokens(pair.Value),
            "run `workspace orca sync --apply`, or set this agent in Orca Settings → Agents"))];
    }

    /// <summary>
    /// The launch line to send for each policy agent whose live one differs. Orca replaces <c>agentDefaultArgs</c> whole,
    /// so callers send the live map with these merged in.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LaunchLines(
        AgentPermissionsPolicy policy, IReadOnlyDictionary<string, string> customArgs, IReadOnlyDictionary<string, string> launch)
    {
        var lines = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (agent, line) in WantedLines(policy, customArgs))
        {
            if (Tokens(line) != Tokens(launch.GetValueOrDefault(agent, "")))
            {
                lines[agent] = line;
            }
        }

        return lines;
    }

    private static Dictionary<string, string> WantedLines(AgentPermissionsPolicy policy, IReadOnlyDictionary<string, string> customArgs)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(customArgs);
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var agent in customArgs.Keys.Concat(policy.Agents.Keys).Distinct(StringComparer.Ordinal))
        {
            var added = IsConfigurable(agent) ? FlagsFor(policy.Wanted(agent), agent) : "";
            if (added is not null)
            {
                lines[agent] = $"{added} {customArgs.GetValueOrDefault(agent, "").Trim()}".Trim();
            }
        }

        return lines;
    }

    private static string Tokens(string line) =>
        string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static Dictionary<string, string> With(IEnumerable<string> agents, string flags, params (string Agent, string Flags)[] others)
    {
        var map = agents.ToDictionary(agent => agent, _ => flags, StringComparer.Ordinal);
        foreach (var (agent, value) in others)
        {
            map[agent] = value;
        }

        return map;
    }
}
