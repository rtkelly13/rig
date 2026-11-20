namespace Rig.Core.Devenv;

/// <summary>
/// Global instructions for OpenAI Codex in <c>~/.codex/AGENTS.md</c>: the file Codex reads from its home
/// directory for every repository. Only the block between the markers is managed, so personal notes and
/// other tools' sections survive a refresh.
/// </summary>
public static class CodexRules
{
    /// <summary>Opens the managed block.</summary>
    public const string Begin = RigGlobalContext.Begin;

    /// <summary>Closes the managed block.</summary>
    public const string End = RigGlobalContext.End;

    /// <summary>The global rig availability context injected for Codex sessions.</summary>
    public const string ManagedBlock = RigGlobalContext.ManagedBlock;

    /// <summary>Default header for a newly created instructions file.</summary>
    public const string Header = """
        # Rig Global Rules

        Managed by `rig devenv codex`.
        Edit outside the markers — content there is preserved across runs.
        """;

    /// <summary>Default file path under <paramref name="home"/>: <c>~/.codex/AGENTS.md</c>.</summary>
    public static string FilePath(string home) => Path.Join(home, ".codex", "AGENTS.md");

    /// <summary>Codex reads <c>AGENTS.override.md</c> instead when it exists, so the managed file is shadowed.</summary>
    public static string OverridePath(string home) => Path.Join(home, ".codex", "AGENTS.override.md");

    /// <summary>The managed block, marker-wrapped with trailing newline.</summary>
    public static string Render() => RigGlobalContext.Render();

    /// <summary>
    /// Splices the managed block into <paramref name="existing"/>, or starts a new file when null.
    /// Preserves everything outside the markers.
    /// </summary>
    public static ManagedRulesMerge Merge(string? existing) => RigGlobalContext.Merge(existing, Header);

    /// <summary>Writes the rules content to <paramref name="path"/> using an atomic staging file.</summary>
    public static void Write(string path, string content, bool windows) => RigGlobalContext.Write(path, content, windows);
}
