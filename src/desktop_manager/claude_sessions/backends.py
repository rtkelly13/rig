"""How `recover` gets a planned session running: which terminal it drives, if any.

A backend does two things. It reports the terminal windows sessions can go into, which
`plan.assign_slots` fills, and it carries out the resulting slots.

- **Windows Terminal** (Windows): types each resume command into a tab of an open
  window via UI Automation, and verifies every session comes up (see `recover`).
- **Print** (everywhere else, or `recover --print`): drives nothing. It reports no
  windows, so every session plans into a terminal of its own, and prints each one's
  resume command for the user to paste.

Windows-only imports stay in `wt_uia` and `recover`, which only the Windows backend
loads, and only when it is constructed.
"""

from __future__ import annotations

import sys
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING, Protocol

import typer

from desktop_manager.claude_sessions.discovery import LivenessCheck, claude_home, process_is_alive
from desktop_manager.claude_sessions.outcomes import FreshOutcome, Outcome
from desktop_manager.claude_sessions.plan import ShellKind, Slot, resume_command
from desktop_manager.claude_sessions.startup import FreshTarget
from desktop_manager.claude_sessions.terminal import TerminalWindow
from rig.platforms import Platform, get_current_platform

if TYPE_CHECKING:
    from desktop_manager.claude_sessions.wt_uia import WindowsTerminalUia


@dataclass(frozen=True)
class ResumeOptions:
    claude_args: Sequence[str] = ()
    max_starting: int = 4
    stagger_seconds: float = 0.0
    verify_timeout: float = 60.0


class TerminalBackend(Protocol):
    #: True when `resume` types into a real terminal (takes focus, verifies each session);
    #: False when it only prints the commands.
    drives_terminal: bool

    def list_windows(self) -> list[TerminalWindow]: ...

    def resume(
        self, slots: Sequence[Slot], *, options: ResumeOptions, on_event: Callable[[str], None]
    ) -> list[Outcome]: ...

    def start_fresh(
        self, targets: Sequence[FreshTarget], *, verify_timeout: float, on_event: Callable[[str], None]
    ) -> list[FreshOutcome]: ...


class WindowsTerminalBackend:
    """Resumes sessions in tabs of the open Windows Terminal. Windows only."""

    drives_terminal: bool = True

    def __init__(
        self,
        uia: WindowsTerminalUia | None = None,
        *,
        sessions_dir: Path | None = None,
        is_alive: LivenessCheck = process_is_alive,
    ) -> None:
        if uia is None:
            from desktop_manager.claude_sessions.wt_uia import WindowsTerminalUia

            uia = WindowsTerminalUia()
        self._uia: WindowsTerminalUia = uia
        self._sessions_dir: Path = sessions_dir or claude_home() / "sessions"
        self._is_alive: LivenessCheck = is_alive

    def list_windows(self) -> list[TerminalWindow]:
        return self._uia.list_windows()

    def resume(
        self, slots: Sequence[Slot], *, options: ResumeOptions, on_event: Callable[[str], None]
    ) -> list[Outcome]:
        from desktop_manager.claude_sessions.recover import execute

        return execute(
            slots,
            uia=self._uia,
            sessions_dir=self._sessions_dir,
            is_alive=self._is_alive,
            claude_args=options.claude_args,
            max_starting=options.max_starting,
            stagger_seconds=options.stagger_seconds,
            verify_timeout=options.verify_timeout,
            on_event=on_event,
        )

    def start_fresh(
        self, targets: Sequence[FreshTarget], *, verify_timeout: float, on_event: Callable[[str], None]
    ) -> list[FreshOutcome]:
        from desktop_manager.claude_sessions.recover import start_fresh

        return start_fresh(targets, uia=self._uia, verify_timeout=verify_timeout, on_event=on_event)


def _default_shell() -> ShellKind:
    return "powershell" if sys.platform == "win32" else "posix"


class PrintBackend:
    """Prints each session's resume command, one terminal per session. Never launches anything."""

    drives_terminal: bool = False

    def __init__(self, *, shell: ShellKind | None = None, write: Callable[[str], None] = typer.echo) -> None:
        self._shell: ShellKind = shell or _default_shell()
        self._write: Callable[[str], None] = write

    def list_windows(self) -> list[TerminalWindow]:
        return []

    def resume(
        self,
        slots: Sequence[Slot],
        *,
        options: ResumeOptions,
        on_event: Callable[[str], None],
    ) -> list[Outcome]:
        del on_event  # the protocol's; printing has no progress to report
        self._write(f"# {len(slots)} session(s). Run each command in a terminal of its own.")
        for slot in slots:
            session = slot.session
            self._write("")
            self._write(f"# {session.session_id[:8]}  {session.label[:60]}")
            self._write(
                resume_command(
                    session,
                    shell=self._shell,  # nosec B604 - a terminal shell kind, not subprocess
                    claude_args=options.claude_args,
                )
            )
        return [Outcome(slot, ok=True, detail="printed") for slot in slots]

    def start_fresh(
        self, targets: Sequence[FreshTarget], *, verify_timeout: float, on_event: Callable[[str], None]
    ) -> list[FreshOutcome]:
        del verify_timeout, on_event  # the protocol's; printing starts nothing
        for target in targets:
            self._write("")
            self._write(f"# fresh session for {target.terminal.name}")
            self._write(target.terminal.command_line())
        return [FreshOutcome(target, ok=True, detail="printed") for target in targets]


def select_backend(*, print_only: bool = False) -> TerminalBackend:
    """Windows Terminal on Windows unless `print_only`; the print backend everywhere else."""
    if print_only or get_current_platform() is not Platform.WINDOWS:
        return PrintBackend()
    return WindowsTerminalBackend()
