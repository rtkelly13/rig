"""Where the scheduled Chrome Tabs run is installed and logs, and how long it may take."""

from __future__ import annotations

import os
from pathlib import Path

LOG_NAME = "organise.log"
DEFAULT_WAIT_MINUTES = 45
TIME_LIMIT_MINUTES = 55


def install_root() -> Path:
    """Where the scheduled run logs, and where older installs put a snapshot environment."""
    base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
    return Path(base) / "shared-utilities" / "chrome-tabs"
