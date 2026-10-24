"""Per-tool agent instruction files that `AGENTS.md` replaced.

Most tools that shipped their own instruction file before `AGENTS.md` was a
standard still look for that file first. A copy therefore *shadows* the real
instructions instead of supplementing them, and a pointer stub only adds
indirection — the same failure that removed `CLAUDE.md`.

This tuple is the single source of truth for both halves of the policy:
`agents_md_audit` fails a repository that still carries one, and
`gitignore_manager` writes the same paths into `.gitignore`, so a tool cannot
reintroduce one behind the audit's back.

Deliberately excluded:

- Tool *feature* definitions (`.github/agents/`, `.github/prompts/`,
  `.github/chatmodes/`, `.github/skills/`, `.claude/skills/`,
  `.agents/skills/`). They extend a tool rather than competing with
  `AGENTS.md`, so deleting them would remove working integrations.
- `CONVENTIONS.md` and `.rules`, whose names are generic enough that a
  legitimate non-agent file would be both flagged and hidden from git.
"""

from __future__ import annotations

BESPOKE_AGENT_FILES: tuple[str, ...] = (
    ".github/copilot-instructions.md",  # GitHub Copilot
    ".github/instructions/",  # GitHub Copilot scoped instructions
    ".cursorrules",  # Cursor, legacy single file
    ".cursor/rules/",  # Cursor
    ".windsurfrules",  # Windsurf, legacy single file
    ".windsurf/rules/",  # Windsurf
    ".clinerules",  # Cline, file or directory
    "GEMINI.md",  # Gemini CLI
    "codex.md",  # OpenAI Codex CLI
    ".aider.conf.yml",  # Aider
    ".aiderignore",  # Aider
    ".continuerules",  # Continue, legacy single file
    ".continue/rules/",  # Continue
    ".roorules",  # Roo Code, legacy single file
    ".roo/rules/",  # Roo Code
    ".kiro/steering/",  # Kiro
    ".amazonq/rules/",  # Amazon Q Developer
    ".junie/guidelines.md",  # JetBrains Junie
    "AGENT.md",  # singular variant used by a few older tools
)

#: Name of the derived `.gitignore` entry, so a repo override can replace it.
GITIGNORE_ENTRY_NAME = "bespoke_agent_files"

GITIGNORE_COMMENT = (
    "Bespoke agent instruction files. AGENTS.md is the single source of truth, and a per-tool copy shadows it."
)
