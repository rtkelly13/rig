"""Pin the remaining Python display-guard XML; coordinator policy is covered by the C# oracle tests."""

from __future__ import annotations

from pathlib import Path

from desktop_manager.display_guard import render_guard_task_xml

_USER = "CORP\\me & you"
_REPO = Path("/repo")
_CONFIG = Path("/plans/plan & co.json")


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


def test_the_display_guard_task_is_unchanged() -> None:
    xml = render_guard_task_xml(_REPO, _CONFIG, user_id=_USER, task_name="Shared Utilities\\Display Guard")

    assert xml == _GUARD.format(
        guard_pythonw=_escaped(_REPO / ".venv" / "Scripts" / "pythonw.exe"),
        config=_escaped(_CONFIG),
        repo=_escaped(_REPO),
    )
