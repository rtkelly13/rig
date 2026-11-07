"""Register the coordinator's scheduled tasks, all under one Task Scheduler folder.

`FOLDER` holds everything that keeps this machine aligned:

- `Logon`: at logon, `scheduled logon` (restore first, then due housekeeping);
- `Tick`: every `TICK_MINUTES`, `scheduled tick` (Chrome tabs, due housekeeping);
- `Display Guard`: the session-long `workspace guard`, unchanged.

Both runs go through `pythonw.exe`, as the logged-on user only (the jobs need
their desktop). Installing retires the separate tasks these replace, so nothing
runs twice.
"""

from __future__ import annotations

import subprocess
import tempfile
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from desktop_manager import scheduled_tasks as tasks
from desktop_manager.jobs.registry import TICK_MINUTES, Trigger

FOLDER = "Shared Utilities"
LOGON_TASK = f"{FOLDER}\\Logon"
TICK_TASK = f"{FOLDER}\\Tick"
GUARD_TASK = f"{FOLDER}\\Display Guard"
#: The separate tasks the coordinator replaces, removed by install.
RETIRED_TASKS = ("Workspace Restore", "Chrome Tabs Organise", "Chrome Tabs Prune", "Workspace Display Guard")
#: Cap on one run: restore, then every job, each inside its own timeout.
RUN_LIMIT = "PT2H"


@dataclass(frozen=True, slots=True)
class TaskSpec:
    name: str
    description: str
    trigger: Trigger
    priority: int


LOGON = TaskSpec(
    name=LOGON_TASK,
    description="Restore the workspace, then run due housekeeping (desktop-manager jobs, logon).",
    trigger=Trigger.LOGON,
    # 7 (the default) also means low I/O priority, which starved the post-logon restore.
    priority=4,
)
TICK = TaskSpec(
    name=TICK_TASK,
    description=f"Every {TICK_MINUTES} minutes: tidy Chrome tabs and run due housekeeping (desktop-manager jobs).",
    trigger=Trigger.TICK,
    priority=7,
)


def render_task_xml(spec: TaskSpec, pythonw: Path, *, user_id: str, start: datetime) -> str:
    """Task Scheduler XML running `scheduled <trigger>` under `pythonw`.

    IgnoreNew so a slow run never overlaps the next; the runner's lock covers a
    logon run and a tick that land together.
    """
    logon = spec.trigger is Trigger.LOGON
    return tasks.render_task_xml(
        tasks.ScheduledTask(
            uri=spec.name,
            description=spec.description,
            user_id=user_id,
            trigger=(
                tasks.logon_trigger(user_id, delay="PT5S")
                if logon
                else tasks.repeating_trigger(start, interval=f"PT{TICK_MINUTES}M")
            ),
            command=str(pythonw),
            arguments=f"-m desktop_manager.jobs.scheduled {spec.trigger}",
            execution_time_limit=RUN_LIMIT,
            priority=spec.priority,
            start_when_available=logon,
        )
    )


def register(name: str, xml: str) -> None:
    with tempfile.TemporaryDirectory() as scratch:
        xml_path = Path(scratch) / "task.xml"
        xml_path.write_text(xml, encoding="utf-16")
        _run(["schtasks", "/Create", "/TN", name, "/XML", str(xml_path), "/F"])


def start(name: str) -> None:
    _run(["schtasks", "/Run", "/TN", name])


def delete(name: str) -> bool:
    result = subprocess.run(["schtasks", "/Delete", "/TN", name, "/F"], capture_output=True, text=True, check=False)
    return result.returncode == 0


def exists(name: str) -> bool:
    result = subprocess.run(["schtasks", "/Query", "/TN", name], capture_output=True, text=True, check=False)
    return result.returncode == 0


def _run(command: list[str]) -> None:
    result = subprocess.run(command, capture_output=True, text=True, check=False)
    if result.returncode != 0:
        raise RuntimeError(
            f"{' '.join(command)} failed ({result.returncode}): {(result.stderr or result.stdout).strip()}"
        )
