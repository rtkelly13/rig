"""Task Scheduler XML for the tasks rig registers, rendered in one place.

Every task runs a command as the logged-on user (an interactive token, least
privilege), ignores a new start while one is running, and never stops for
battery. A :class:`ScheduledTask` names what differs between them: the trigger, the
command, and the settings a long-running task needs. Pure string building, so it
imports and renders on any OS; only registering the result needs Windows.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime


def xml_escape(value: str) -> str:
    return value.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


def logon_trigger(user_id: str, *, delay: str) -> str:
    """A trigger that fires when ``user_id`` logs on, after ``delay`` (an ISO 8601 duration)."""
    return (
        "    <LogonTrigger>\n"
        "      <Enabled>true</Enabled>\n"
        f"      <UserId>{xml_escape(user_id)}</UserId>\n"
        f"      <Delay>{delay}</Delay>\n"
        "    </LogonTrigger>\n"
    )


def repeating_trigger(start: datetime, *, interval: str) -> str:
    """A trigger that fires at ``start`` and then every ``interval``, indefinitely."""
    return (
        "    <TimeTrigger>\n"
        f"      <StartBoundary>{start.strftime('%Y-%m-%dT%H:%M:%S')}</StartBoundary>\n"
        "      <Enabled>true</Enabled>\n"
        "      <Repetition>\n"
        f"        <Interval>{interval}</Interval>\n"
        "        <StopAtDurationEnd>false</StopAtDurationEnd>\n"
        "      </Repetition>\n"
        "    </TimeTrigger>\n"
    )


@dataclass(frozen=True, slots=True)
class RestartOnFailure:
    interval: str
    count: int


@dataclass(frozen=True, slots=True)
class ScheduledTask:
    """One scheduled task. ``trigger`` is a rendered trigger element (see the builders above)."""

    uri: str
    description: str
    user_id: str
    trigger: str
    command: str
    arguments: str
    #: An ISO 8601 duration; ``PT0S`` means no limit.
    execution_time_limit: str
    priority: int
    start_when_available: bool = True
    restart_on_failure: RestartOnFailure | None = None
    working_directory: str | None = None


def render_task_xml(spec: ScheduledTask) -> str:
    user = xml_escape(spec.user_id)
    restart = (
        "    <RestartOnFailure>\n"
        f"      <Interval>{spec.restart_on_failure.interval}</Interval>\n"
        f"      <Count>{spec.restart_on_failure.count}</Count>\n"
        "    </RestartOnFailure>\n"
        if spec.restart_on_failure is not None
        else ""
    )
    working_directory = (
        f"      <WorkingDirectory>{xml_escape(spec.working_directory)}</WorkingDirectory>\n"
        if spec.working_directory is not None
        else ""
    )
    return (
        '<?xml version="1.0" encoding="UTF-16"?>\n'
        '<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">\n'
        "  <RegistrationInfo>\n"
        f"    <Description>{xml_escape(spec.description)}</Description>\n"
        f"    <URI>\\{xml_escape(spec.uri)}</URI>\n"
        "  </RegistrationInfo>\n"
        "  <Triggers>\n"
        f"{spec.trigger}"
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
        f"    <StartWhenAvailable>{'true' if spec.start_when_available else 'false'}</StartWhenAvailable>\n"
        "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\n"
        "    <AllowStartOnDemand>true</AllowStartOnDemand>\n"
        "    <Enabled>true</Enabled>\n"
        "    <Hidden>false</Hidden>\n"
        "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n"
        "    <WakeToRun>false</WakeToRun>\n"
        f"    <ExecutionTimeLimit>{spec.execution_time_limit}</ExecutionTimeLimit>\n"
        f"    <Priority>{spec.priority}</Priority>\n"
        f"{restart}"
        "  </Settings>\n"
        '  <Actions Context="Author">\n'
        "    <Exec>\n"
        f"      <Command>{xml_escape(spec.command)}</Command>\n"
        f"      <Arguments>{xml_escape(spec.arguments)}</Arguments>\n"
        f"{working_directory}"
        "    </Exec>\n"
        "  </Actions>\n"
        "</Task>\n"
    )
