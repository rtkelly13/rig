"""Scheduled-task XML is rendered once, in ``scheduled_tasks``, for every task rig registers.

The expected documents are what the two hand-built writers produced before they
shared a renderer, byte for byte, so a change here is a change to the registered
task.
"""

from __future__ import annotations

from datetime import datetime
from pathlib import Path

from desktop_manager.display_guard import render_guard_task_xml
from desktop_manager.jobs import install

_USER = "CORP\\me & you"
_REPO = Path("/repo")
_CONFIG = Path("/plans/plan & co.json")
_PYTHONW = Path("/rt/.venv/Scripts/pythonw.exe")


def _escaped(path: Path) -> str:
    return str(path).replace("&", "&amp;")


_GUARD = r"""<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Put windows back after a monitor drops out or the machine wakes.</Description>
    <URI>\Shared Utilities\Display Guard</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>CORP\me &amp; you</UserId>
      <Delay>PT30S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>CORP\me &amp; you</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
    <RestartOnFailure>
      <Interval>PT1M</Interval>
      <Count>10</Count>
    </RestartOnFailure>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{guard_pythonw}</Command>
      <Arguments>-m desktop_manager.cli workspace guard --config &quot;{config}&quot;</Arguments>
      <WorkingDirectory>{repo}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"""

_LOGON = r"""<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Restore the workspace, then run due housekeeping (desktop-manager jobs, logon).</Description>
    <URI>\Shared Utilities\Logon</URI>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>CORP\me &amp; you</UserId>
      <Delay>PT5S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>CORP\me &amp; you</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{pythonw}</Command>
      <Arguments>-m desktop_manager.jobs.scheduled logon</Arguments>
    </Exec>
  </Actions>
</Task>
"""

_TICK = r"""<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Every 15 minutes: tidy Chrome tabs and run due housekeeping (desktop-manager jobs).</Description>
    <URI>\Shared Utilities\Tick</URI>
  </RegistrationInfo>
  <Triggers>
    <TimeTrigger>
      <StartBoundary>2026-10-10T09:00:00</StartBoundary>
      <Enabled>true</Enabled>
      <Repetition>
        <Interval>PT15M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>CORP\me &amp; you</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{pythonw}</Command>
      <Arguments>-m desktop_manager.jobs.scheduled tick</Arguments>
    </Exec>
  </Actions>
</Task>
"""


def test_the_display_guard_task_is_unchanged() -> None:
    xml = render_guard_task_xml(_REPO, _CONFIG, user_id=_USER, task_name=install.GUARD_TASK)

    assert xml == _GUARD.format(
        guard_pythonw=_escaped(_REPO / ".venv" / "Scripts" / "pythonw.exe"),
        config=_escaped(_CONFIG),
        repo=_escaped(_REPO),
    )


def test_the_logon_task_is_unchanged() -> None:
    xml = install.render_task_xml(install.LOGON, _PYTHONW, user_id=_USER, start=datetime(2026, 10, 10, 9, 0))

    assert xml == _LOGON.format(pythonw=_escaped(_PYTHONW))


def test_the_tick_task_is_unchanged() -> None:
    xml = install.render_task_xml(install.TICK, _PYTHONW, user_id=_USER, start=datetime(2026, 10, 10, 9, 0))

    assert xml == _TICK.format(pythonw=_escaped(_PYTHONW))
