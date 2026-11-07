"""The macOS LaunchAgent that keeps the collector running.

Unlike a scheduled agent this one is resident (`KeepAlive`), because
tools send telemetry whenever they run. That is affordable only because of what
the collector does at rest: measured at 3 MB resident and 0.03 s of CPU per two
idle minutes (see `collector`). `ProcessType` Background, `LowPriorityIO` and
`Nice` keep macOS treating it like background housekeeping, and
`ThrottleInterval` stops a broken config turning into a tight restart loop.
"""

from __future__ import annotations

import pathlib
import plistlib

from rig import launchd
from rig.otel_stack.paths import log_path

LABEL = "dev.rtkelly.rig.otelcol"
#: Seconds launchd waits before restarting a collector that exited.
THROTTLE_SECONDS = 60


def plist_path() -> pathlib.Path:
    return launchd.agents_dir() / f"{LABEL}.plist"


def render(binary: pathlib.Path, config: pathlib.Path) -> bytes:
    log = str(log_path())
    agent = {
        "Label": LABEL,
        "ProgramArguments": [str(binary), "--config", str(config)],
        "RunAtLoad": True,
        "KeepAlive": True,
        "ThrottleInterval": THROTTLE_SECONDS,
        "ProcessType": "Background",
        "LowPriorityIO": True,
        "Nice": 10,
        "StandardOutPath": log,
        "StandardErrorPath": log,
    }
    return plistlib.dumps(agent)


def install(binary: pathlib.Path, config: pathlib.Path) -> pathlib.Path:
    path = plist_path()
    log_path().parent.mkdir(parents=True, exist_ok=True)
    launchd.load(path, render(binary, config))
    return path


def uninstall() -> bool:
    return launchd.remove(plist_path())


def installed() -> bool:
    return plist_path().is_file()


def loaded() -> bool:
    return launchd.loaded(LABEL)
