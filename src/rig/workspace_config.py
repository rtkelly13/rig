"""This machine's `~/.workspace/config.json`: settings that differ between machines.

`WORKSPACE_HOME` overrides the folder. A missing or malformed file reads as empty, so
every setting here falls back to its default.

`tools.estate.skip_repos` lists managed-project names (as in
`config/managed-projects.json`) that must never be cloned or registered in Orca on this
machine, while other machines still get them:

`tools.estate.work_dirs` lists the folders holding this machine's work repos: a name is a
folder under the code root, an absolute path is used as it is. None listed means no work scope:
`estate wrapup --work` selects nothing and `estate decisions` scans only the personal estate.

`tools.estate.workspace_repo` is the work workspace checkout whose `workspace_cli` the scheduled
`worktree-reclaim` and `workspace-maintain` jobs run, resolved like a work folder. Unset, those
jobs are not registered.

`tools.desktop.aliases` overrides or adds aliases in the desktop workspace plan, such as
`chrome-profile-work` or `workspace-root`, so the tracked plan holds no machine's paths.

```json
{
  "tools": {
    "estate": { "skip_repos": ["hearth"], "work_dirs": ["acme", "~/work/acme"], "workspace_repo": "workspace" },
    "desktop": { "aliases": { "chrome-profile-work": "Profile 4" } }
  }
}
```
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from pydantic import StrictStr

from rig.typed_json import Lenient, try_decode_json

#: Where the skip list lives, as shown in messages.
SKIP_REPOS_KEY = "tools.estate.skip_repos"


def workspace_config_path() -> Path:
    """`~/.workspace/config.json`, or `config.json` in `WORKSPACE_HOME` when that is set."""
    configured = os.environ.get("WORKSPACE_HOME")
    return (Path(configured).expanduser() if configured else Path.home() / ".workspace") / "config.json"


@dataclass(frozen=True, slots=True)
class _EstateConfig:
    skip_repos: Lenient[tuple[StrictStr, ...]] = None
    work_dirs: Lenient[tuple[StrictStr, ...]] = None
    workspace_repo: Lenient[StrictStr] = None


@dataclass(frozen=True, slots=True)
class _DesktopConfig:
    aliases: Lenient[dict[StrictStr, StrictStr]] = None


@dataclass(frozen=True, slots=True)
class _Tools:
    estate: Lenient[_EstateConfig] = None
    desktop: Lenient[_DesktopConfig] = None


@dataclass(frozen=True, slots=True)
class _WorkspaceConfig:
    tools: Lenient[_Tools] = None


def _tools(config_path: Path | None) -> _Tools:
    try:
        text = (config_path or workspace_config_path()).read_text(encoding="utf-8")
    except OSError:
        return _Tools()
    config = try_decode_json(_WorkspaceConfig, text)
    return (config.tools if config else None) or _Tools()


def _estate(config_path: Path | None) -> _EstateConfig:
    return _tools(config_path).estate or _EstateConfig()


def load_skip_repos(config_path: Path | None = None) -> frozenset[str]:
    """The managed-project names this machine never clones or registers; empty when none are listed."""
    return frozenset(name.strip() for name in _estate(config_path).skip_repos or () if name.strip())


def load_work_dirs(config_path: Path | None = None) -> tuple[str, ...]:
    """This machine's work folders as written (names or paths); empty when none are listed."""
    return tuple(name.strip() for name in _estate(config_path).work_dirs or () if name.strip())


def load_workspace_repo(config_path: Path | None = None) -> str | None:
    """The work workspace checkout as written (a name or a path), or None when unset."""
    configured = (_estate(config_path).workspace_repo or "").strip()
    return configured or None


def load_desktop_aliases(config_path: Path | None = None) -> dict[str, str]:
    """This machine's desktop plan aliases, applied over the plan's own; empty when none are set."""
    desktop = _tools(config_path).desktop
    aliases = (desktop.aliases if desktop else None) or {}
    return {name.strip(): value for name, value in aliases.items() if name.strip()}


def skip_reason() -> str:
    """Why a skipped repo is left alone, for action lines and findings."""
    return f"listed in {SKIP_REPOS_KEY} on this machine"
