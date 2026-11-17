"""Where the pinned runtime copy lives, for the Python code the scheduled tasks still run.

`rig runtime install` (C#, `Rig.Core.Runtime.RuntimeManager`) builds the copies and moves the
`current` link; this module only finds them:

    %LOCALAPPDATA%\\shared-utilities\\runtime\\
        <sha12>\\          one commit's files plus its own `.venv` and published `rig`
        current            directory junction to the copy in use
        state.json         current and previous copy, and the repo they came from
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

from pydantic import StrictStr

from rig.roots import app_data_dir
from rig.typed_json import decode_json

LINK_NAME = "current"
STATE_NAME = "state.json"
PLAN_RELATIVE = Path("desktop-manager") / "workspace-plan.bundle.json"


def base_dir() -> Path:
    return app_data_dir()


def runtime_root() -> Path:
    return base_dir() / "runtime"


def current_link(root: Path | None = None) -> Path:
    return (root or runtime_root()) / LINK_NAME


def traces_dir() -> Path:
    """Where tools run from a copy write their traces: outside every copy, so they outlive it."""
    return base_dir() / "traces"


def workspace_logs_dir() -> Path:
    """Where Workspace Restore logs when it runs from a copy: outside every copy, so history survives an update."""
    return base_dir() / "workspace-restore" / "logs"


def running_copy(module_file: str, root: Path | None = None) -> bool:
    """Whether `module_file` belongs to an installed copy rather than a checkout."""
    module = os.path.normcase(os.path.realpath(module_file))
    base = os.path.normcase(os.path.realpath(root or runtime_root()))
    try:
        return os.path.commonpath([module, base]) == base
    except ValueError:
        return False


@dataclass(frozen=True, slots=True)
class State:
    current: StrictStr | None = None
    previous: StrictStr | None = None
    repo: StrictStr | None = None


def read_state(root: Path) -> State:
    path = root / STATE_NAME
    if not path.is_file():
        return State()
    return decode_json(State, path.read_text(encoding="utf-8"))
