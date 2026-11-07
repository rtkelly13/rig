"""The `agents_md_hygiene` policy block: budgets for `AGENTS.md` and the docs tree.

A leaf module so `policy_config` stays under the ratchet's module-length limit.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal


@dataclass(frozen=True)
class AgentsMdRules:
    """Budgets and shape requirements for `AGENTS.md` and the docs tree."""

    max_lines: int = 120
    claude_md: Literal["symlink", "absent", "any"] = "any"
    require: tuple[str, ...] = ("description", "package_manager", "commands")
    check_paths: bool = True
    report_only: tuple[str, ...] = ("instruction-count", "forcing-language", "section-outlier")
    docs_index_min_files: int = 5
    max_code_block_lines: int = 20
