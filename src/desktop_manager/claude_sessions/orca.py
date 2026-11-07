"""Folders whose Claude sessions Orca resumes, so claude-sessions leaves them alone.

The repos set up for Orca are listed by hand in `~/.workspace/config.json`
(`tools.orca.repos`), keyed by paths relative to the code root, such as
`personal/shared-utilities`; `uv run workspace orca check` in the workspace repo
validates the list. Orca's own worktrees live under `~/orca/workspaces`. Orca tracks and
resumes the sessions in all of these, so resuming them here as well would open them twice.
"""

from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath

from pydantic import StrictStr

from rig import roots
from rig.typed_json import Lenient, try_decode_json
from rig.workspace_config import workspace_config_path


@dataclass(frozen=True, slots=True)
class OrcaFolders:
    roots: tuple[Path, ...] = ()

    def owner(self, path: str | None) -> Path | None:
        """The Orca folder `path` sits in, or None when Orca does not own it."""
        if not path:
            return None
        candidate = _comparable(Path(path))
        return next((root for root in self.roots if _is_within(candidate, _comparable(root))), None)


def default_worktree_root() -> Path:
    return Path.home() / "orca" / "workspaces"


@dataclass(frozen=True, slots=True)
class _OrcaConfig:
    repos: Lenient[dict[StrictStr, object]] = None


@dataclass(frozen=True, slots=True)
class _Tools:
    orca: Lenient[_OrcaConfig] = None


@dataclass(frozen=True, slots=True)
class _WorkspaceConfig:
    tools: Lenient[_Tools] = None


def load_orca_folders(config_path: Path | None = None, code_root: Path | None = None) -> OrcaFolders:
    """The listed repos plus Orca's worktree folder. No listed repos means Orca owns nothing.

    Keys that are not relative paths inside the code root are ignored; `workspace orca
    check` reports them.
    """
    try:
        text = (config_path or workspace_config_path()).read_text(encoding="utf-8")
    except OSError:
        return OrcaFolders()
    config = try_decode_json(_WorkspaceConfig, text)
    orca = config.tools.orca if config and config.tools else None
    if orca is None or not orca.repos:
        return OrcaFolders()
    base = code_root or roots.code_root()
    repos = tuple(base / key for key in orca.repos if _is_relative_inside(key))
    return OrcaFolders(roots=(*repos, default_worktree_root())) if repos else OrcaFolders()


def _is_relative_inside(key: str) -> bool:
    for flavour in (PurePosixPath(key), PureWindowsPath(key)):
        if flavour.is_absolute() or flavour.anchor:
            return False
    return ".." not in PureWindowsPath(key).parts and key.strip() not in {"", "."}


def _comparable(path: Path) -> str:
    text = os.path.normpath(str(path.expanduser()))
    return text.casefold() if sys.platform == "win32" else text


def _is_within(path: str, root: str) -> bool:
    return path == root or path.startswith(root.rstrip("\\/") + os.sep)
