"""Where an Orca worktree's setup and archive scripts come from.

Orca merges two sources. A repo's committed `orca.yaml` (``scripts.setup``/``scripts.archive``)
is the shared source, read from the new worktree. Orca's own per-repo ``hookSettings`` are the
local source. ``commandSourcePolicy`` picks between them; when it is unset, any local script
wins and `orca.yaml` is ignored. This module mirrors that resolution so the doctor can say
which script a new worktree will actually run.
"""

from __future__ import annotations

from dataclasses import dataclass, replace
from pathlib import Path
from typing import Annotated, Literal

from pydantic import Field

from rig.typed_json import Lenient
from rig.typed_yaml import try_decode_yaml

type ScriptKind = Literal["setup", "archive"]
type SourcePolicy = Literal["local-only", "shared-only", "run-both"]

ORCA_YAML = "orca.yaml"


@dataclass(slots=True, frozen=True)
class Scripts:
    setup: str = ""
    archive: str = ""

    def of(self, kind: ScriptKind) -> str:
        return self.setup if kind == "setup" else self.archive


@dataclass(slots=True, frozen=True)
class HookSettings:
    """Orca's per-repo ``hookSettings``. ``repo.update`` replaces it whole, so writes must carry every field."""

    mode: str | None = None
    setup_run_policy: Annotated[str | None, Field(alias="setupRunPolicy")] = None
    setup_agent_startup_policy: Annotated[str | None, Field(alias="setupAgentStartupPolicy")] = None
    scripts: Lenient[Scripts] = None
    command_source_policy: Annotated[str | None, Field(alias="commandSourcePolicy")] = None


@dataclass(slots=True, frozen=True)
class _OrcaYaml:
    scripts: Lenient[Scripts] = None


def read_orca_yaml(repo_path: Path) -> Scripts | None:
    """The scripts a repo's `orca.yaml` declares, or None when it has no readable file."""
    path = repo_path / ORCA_YAML
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return None
    parsed = try_decode_yaml(_OrcaYaml, text)
    return (parsed.scripts or Scripts()) if parsed else None


@dataclass(slots=True, frozen=True)
class HookSource:
    """One script kind as Orca will resolve it for a new worktree."""

    shared: str
    local: str
    policy: SourcePolicy

    @property
    def effective(self) -> str:
        if self.policy == "local-only":
            return self.local
        if self.policy == "run-both":
            return "\n".join(script for script in (self.shared, self.local) if script)
        return self.shared


def resolve(kind: ScriptKind, local: HookSettings | None, shared: Scripts | None) -> HookSource:
    """Mirror Orca's choice between `orca.yaml` and its local script for ``kind``."""
    local_script = (local.scripts.of(kind) if local and local.scripts else "").strip()
    shared_script = (shared.of(kind) if shared else "").strip()
    return HookSource(shared=shared_script, local=local_script, policy=_policy(local, has_local=bool(local_script)))


def _policy(local: HookSettings | None, *, has_local: bool) -> SourcePolicy:
    configured = local.command_source_policy if local else None
    if configured == "local-only":
        return "local-only"
    if configured == "run-both":
        return "run-both"
    if configured is None and has_local:
        return "local-only"
    return "shared-only"


def defer_to_orca_yaml(local: HookSettings | None) -> HookSettings:
    """``local`` with its scripts cleared and `orca.yaml` as the only source; other fields are kept."""
    return replace(local or HookSettings(), scripts=Scripts(), command_source_policy="shared-only")
