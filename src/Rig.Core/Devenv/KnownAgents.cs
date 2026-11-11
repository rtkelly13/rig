namespace Rig.Core.Devenv;

/// <summary>An AI coding agent devenv looks for.</summary>
/// <param name="Id">Its identifier.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Command">The command it is run by.</param>
public sealed record KnownAgent(string Id, string Name, string Command);

/// <summary>The AI coding agents devenv reports on. Ported from devenv's <c>ai_tools.discovery</c>.</summary>
public static class KnownAgents
{
    /// <summary>Every agent, in report order.</summary>
    public static readonly IReadOnlyList<KnownAgent> All =
    [
        new("antigravity", "Google Antigravity (agy)", "agy"),
        new("claude", "Claude Code", "claude"),
        new("codex", "OpenAI Codex", "codex"),
        new("opencode", "OpenCode", "opencode"),
        new("github-copilot", "GitHub Copilot", "copilot"),
        new("cursor", "Cursor", "cursor"),
        new("aider", "Aider", "aider"),
        new("gemini", "Gemini CLI", "gemini"),
        new("windsurf", "Windsurf", "windsurf"),
    ];
}
