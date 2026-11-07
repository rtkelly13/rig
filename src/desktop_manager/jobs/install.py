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


def _xml_escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def _trigger_xml(spec: TaskSpec, user: str, start: datetime) -> str:
    if spec.trigger is Trigger.LOGON:
        return (
            "    <LogonTrigger>\n"
            "      <Enabled>true</Enabled>\n"
            f"      <UserId>{user}</UserId>\n"
            "      <Delay>PT5S</Delay>\n"
            "    </LogonTrigger>\n"
        )
    return (
        "    <TimeTrigger>\n"
        f"      <StartBoundary>{start.strftime('%Y-%m-%dT%H:%M:%S')}</StartBoundary>\n"
        "      <Enabled>true</Enabled>\n"
        "      <Repetition>\n"
        f"        <Interval>PT{TICK_MINUTES}M</Interval>\n"
        "        <StopAtDurationEnd>false</StopAtDurationEnd>\n"
        "      </Repetition>\n"
        "    </TimeTrigger>\n"
    )


def render_task_xml(spec: TaskSpec, pythonw: Path, *, user_id: str, start: datetime) -> str:
    """Task Scheduler XML running `scheduled <trigger>` under `pythonw`.

    IgnoreNew so a slow run never overlaps the next; the runner's lock covers a
    logon run and a tick that land together.
    """
    user = _xml_escape(user_id)
    return (
        '<?xml version="1.0" encoding="UTF-16"?>\n'
        '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">\n'
        "  <RegistrationInfo>\n"
        f"    <Description>{_xml_escape(spec.description)}</Description>\n"
        f"    <URI>\\{_xml_escape(spec.name)}</URI>\n"
        "  </RegistrationInfo>\n"
        "  <Triggers>\n"
        f"{_trigger_xml(spec, user, start)}"
        "  </Triggers>\n"
        "  <Principals>\n"
        '    <Principal id="Author">\n'
        f"      <UserId>{user}</UserId>\n"
        "      <LogonType>InteractiveToken</LogonType>\n"
        "      <RunLevel>LeastPrivilege</RunLevel>\n"
        "    </Principal>\n"
        "  </Principals>\n"
        "  <Settings>\n"
        "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n"
        "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n"
        "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n"
        "    <AllowHardTerminate>true</AllowHardTerminate>\n"
        f"    <StartWhenAvailable>{'true' if spec.trigger is Trigger.LOGON else 'false'}</StartWhenAvailable>\n"
        "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n"
        "    <AllowStartOnDemand>true</AllowStartOnDemand>\n"
        "    <Enabled>true</Enabled>\n"
        "    <Hidden>false</Hidden>\n"
        "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n"
        "    <WakeToRun>false</WakeToRun>\n"
        f"    <ExecutionTimeLimit>{RUN_LIMIT}</ExecutionTimeLimit>\n"
        f"    <Priority>{spec.priority}</Priority>\n"
        "  </Settings>\n"
        '  <Actions Context="Author">\n'
        "    <Exec>\n"
        f"      <Command>{_xml_escape(str(pythonw))}</Command>\n"
        f"      <Arguments>-m desktop_manager.jobs.scheduled {spec.trigger}</Arguments>\n"
        "    </Exec>\n"
        "  </Actions>\n"
        "</Task>\n"
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
