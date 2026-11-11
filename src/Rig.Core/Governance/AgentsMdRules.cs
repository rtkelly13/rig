namespace Rig.Core.Governance;

/// <summary>
/// The <c>agents_md_hygiene</c> policy block: budgets and shape rules for <c>AGENTS.md</c> and the docs tree. A layer
/// that declares the block declares all of it: a field it leaves out takes the built-in default, not a lower layer's.
/// </summary>
public sealed record AgentsMdRules
{
    /// <summary>The checks reported but never gated, as the Python's defaults name them.</summary>
    public static readonly IReadOnlyList<string> DefaultReportOnly = ["instruction-count", "forcing-language", "section-outlier"];

    /// <summary>The rules with every field at its default.</summary>
    public static AgentsMdRules Default { get; } = new();

    /// <summary>The root file's line budget.</summary>
    public int MaxLines { get; init; } = 120;

    /// <summary>What <c>CLAUDE.md</c> may be: <c>symlink</c>, <c>absent</c> or <c>any</c>.</summary>
    public string ClaudeMd { get; init; } = "any";

    /// <summary>Per-tool instruction files that shadow <c>AGENTS.md</c> when present. Empty disables the check.</summary>
    public IReadOnlyList<string> BespokeAgentFiles { get; init; } = Governance.BespokeAgentFiles.Defaults;

    /// <summary>What the root file must answer: <c>description</c>, <c>package_manager</c>, <c>commands</c>.</summary>
    public IReadOnlyList<string> Require { get; init; } = ["description", "package_manager", "commands"];

    /// <summary>Whether backticked paths in the root file must exist.</summary>
    public bool CheckPaths { get; init; } = true;

    /// <summary>The checks reported but never gated.</summary>
    public IReadOnlyList<string> ReportOnly { get; init; } = DefaultReportOnly;

    /// <summary>How many markdown files <c>docs/</c> holds before it needs an index.</summary>
    public int DocsIndexMinFiles { get; init; } = 5;

    /// <summary>The longest fenced block a doc under <c>docs/</c> may carry.</summary>
    public int MaxCodeBlockLines { get; init; } = 20;
}
