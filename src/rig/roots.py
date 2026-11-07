"""Where things live: the code root, the personal folder, this checkout's config, and per-user app data.

The code root is the folder the repos are cloned into (``~/code`` on this estate). It is
marked by an ``AGENTS.md`` sitting beside a ``personal/`` directory. Resolution order:

1. ``CODE_ROOT`` in the environment;
2. the nearest ancestor of this checkout that carries the marker, which still finds the
   right root from a worktree nested inside a repo (``<root>/personal/rig/temp/rig/x``);
3. ``~/code``, else ``<drive>/code``, whichever exists first.

The ``personal`` folder name is fixed: it is half of the marker that finds the root. Which
other folders and GitHub owners count as personal or work is configuration, in `rig.scopes`.

A leaf module: it imports nothing from ``rig`` so every package can use it.
"""

from __future__ import annotations

import os
from pathlib import Path

import platformdirs

CODE_ROOT_ENV = "CODE_ROOT"
#: Per-user state (runtime copies, traces, ``rig.db``) lives in a folder with this name.
APP_NAME = "shared-utilities"

_MARKER_FILE = "AGENTS.md"
_PERSONAL = "personal"
_CONFIG = Path("config")
_MANAGED_PROJECTS = "managed-projects.json"
#: Retired (archived or deleted) projects, kept beside managed-projects.json and never inside it.
ARCHIVED_PROJECTS = "archived-projects.json"
_CHECKOUT = Path(__file__).resolve().parents[2]


def is_code_root(path: Path) -> bool:
    """Whether *path* carries the code-root marker: an ``AGENTS.md`` beside a ``personal/`` folder."""
    return (path / _MARKER_FILE).is_file() and (path / _PERSONAL).is_dir()


def find_code_root(start: Path) -> Path | None:
    """The nearest of *start* and its ancestors that is a code root, or None."""
    for candidate in (start, *start.parents):
        if is_code_root(candidate):
            return candidate
    return None


def home_code_root(home: Path) -> Path:
    """The fallback code root: ``<home>/code``, else ``<drive>/code``, else ``<home>/code`` anyway."""
    for candidate in (home / "code", Path(home.anchor) / "code"):
        if candidate.is_dir():
            return candidate
    return home / "code"


def code_root(start: Path | None = None, home: Path | None = None) -> Path:
    """The folder holding every clone. *start* and *home* exist for tests."""
    configured = os.environ.get(CODE_ROOT_ENV)
    if configured:
        return Path(configured).expanduser()
    found = find_code_root(start or _CHECKOUT)
    if found is not None:
        return found
    return home_code_root(home or Path.home())


def personal_dir() -> Path:
    """The folder holding the personal repos: ``<code root>/personal``."""
    return code_root() / _PERSONAL


def checkout_config(name: str) -> Path:
    """This checkout's ``config/<name>``, else the one under the working directory."""
    candidate = _CHECKOUT / _CONFIG / name
    if candidate.is_file():
        return candidate
    return Path.cwd() / _CONFIG / name


def managed_projects_config() -> Path:
    """This checkout's ``config/managed-projects.json``, else the one under the working directory."""
    return checkout_config(_MANAGED_PROJECTS)


def app_data_dir() -> Path:
    """Per-user app state: runtime copies, traces and the shared database.

    ``LOCALAPPDATA`` wins on every platform, not only Windows: the test suite points it at a
    temp folder so no test writes into the live install.
    """
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / APP_NAME
    return Path(platformdirs.user_data_dir(APP_NAME, appauthor=False))
