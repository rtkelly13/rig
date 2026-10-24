"""The `agents_md_hygiene` policy block: budgets for `AGENTS.md` and the docs tree.

A leaf module so `policy_config` stays under the ratchet's module-length limit.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal

from rig.repo_governance.agent_files import BESPOKE_AGENT_FILES


@dataclass(frozen=True)
class AgentsMdRules:
    """Budgets and shape requirements for `AGENTS.md` and the docs tree."""

    max_lines: int = 120
    claude_md: Literal["symlink", "absent", "any"] = "any"
    #: Per-tool instruction files that shadow `AGENTS.md` when present. Empty disables the check.
    bespoke_agent_files: tuple[str, ...] = BESPOKE_AGENT_FILES
    require: tuple[str, ...] = ("description", "package_manager", "commands")
    check_paths: bool = True
    report_only: tuple[str, ...] = ("instruction-count", "forcing-language", "section-outlier")
    docs_index_min_files: int = 5
    max_code_block_lines: int = 20
