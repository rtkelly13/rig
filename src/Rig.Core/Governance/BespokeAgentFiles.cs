namespace Rig.Core.Governance;

/// <summary>
/// Per-tool agent instruction files that <c>AGENTS.md</c> replaced. Most tools that shipped their own file before
/// <c>AGENTS.md</c> still read it first, so a copy shadows the real instructions. The gitignore policy writes these
/// paths into <c>.gitignore</c> so a tool cannot reintroduce one. Ported from <c>rig.repo_governance.agent_files</c>,
/// which the AGENTS.md audit still reads until it moves too.
/// </summary>
public static class BespokeAgentFiles
{
    /// <summary>The paths, used when a repo's <c>agents_md_hygiene</c> does not name its own.</summary>
    public static readonly IReadOnlyList<string> Defaults =
    [
        ".github/copilot-instructions.md",
        ".github/instructions/",
        ".cursorrules",
        ".cursor/rules/",
        ".windsurfrules",
        ".windsurf/rules/",
        ".clinerules",
        "GEMINI.md",
        "codex.md",
        ".aider.conf.yml",
        ".aiderignore",
        ".continuerules",
        ".continue/rules/",
        ".roorules",
        ".roo/rules/",
        ".kiro/steering/",
        ".amazonq/rules/",
        ".junie/guidelines.md",
        "AGENT.md",
    ];

    /// <summary>The name of the derived <c>.gitignore</c> entry, so a repo override can replace it.</summary>
    public const string EntryName = "bespoke_agent_files";

    /// <summary>The comment written above the derived entry.</summary>
    public const string Comment =
        "Bespoke agent instruction files. AGENTS.md is the single source of truth, and a per-tool copy shadows it.";
}
