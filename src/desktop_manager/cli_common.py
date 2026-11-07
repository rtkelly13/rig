"""Console, plan-path option and restore-wait rendering shared by the workspace commands."""

from __future__ import annotations

import contextlib
import io
import sys
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from rich.console import Console

from desktop_manager.models import RestoreRunLogger


def _make_output_encoding_safe() -> None:
    """Stop unencodable window titles from crashing redirected output.

    When stdout is piped to a file (e.g. the startup restore log), Python uses
    the locale encoding (cp1252 on Windows) with strict error handling, so a
    window title containing a character like "✳" raises UnicodeEncodeError
    mid-render and aborts the run. Degrade such characters instead.
    """
    for stream in (sys.stdout, sys.stderr):
        if isinstance(stream, io.TextIOWrapper):
            with contextlib.suppress(ValueError):
                stream.reconfigure(errors="replace")


_make_output_encoding_safe()
console = Console()


def console_safe_text(value: str) -> str:
    return value.encode("cp1252", errors="replace").decode("cp1252")


def virtual_desktop_event_callback(logger: RestoreRunLogger | None) -> Callable[[str], None]:
    def _callback(message: str) -> None:
        safe_message = console_safe_text(message)
        console.print(f"[dim]{safe_message}[/dim]")
        if logger is not None:
            logger.log(safe_message)

    return _callback


@dataclass(frozen=True, slots=True)
class _DiffSection:
    desktop: str
    screen: str
    lines: tuple[str, ...]


def _wait_diff_sections(diff: str) -> list[_DiffSection]:
    sections: list[_DiffSection] = []
    current_desktop: str | None = None
    current_screen: str | None = None
    current_lines: list[str] = []

    def flush() -> None:
        nonlocal current_screen, current_lines
        if current_desktop is None or current_screen is None:
            current_lines = []
            return
        sections.append(_DiffSection(current_desktop, current_screen, tuple(current_lines)))
        current_lines = []

    for raw_line in diff.splitlines():
        line = raw_line.rstrip()
        if not line or line == "Workspace Diff":
            continue
        if line.startswith("Screen "):
            flush()
            current_screen = line
            continue
        if not line.startswith(" "):
            flush()
            current_desktop = line
            current_screen = None
            continue
        if current_screen is not None:
            current_lines.append(line)

    flush()
    return sections


@dataclass(frozen=True, slots=True)
class PlannedProcess:
    desktop_name: str
    entry_name: str
    process_name: str | None


@dataclass(frozen=True, slots=True)
class _DiffSectionKey:
    desktop: str
    screen: str


def wait_diff_for_console(diff: str, previous_diff: str | None) -> str:
    if previous_diff is None:
        return diff

    previous_sections = {
        _DiffSectionKey(section.desktop, section.screen): section.lines
        for section in _wait_diff_sections(previous_diff)
    }
    changed_sections = [
        section
        for section in _wait_diff_sections(diff)
        if previous_sections.get(_DiffSectionKey(section.desktop, section.screen)) != section.lines
    ]
    if not changed_sections:
        return diff

    lines = ["Workspace Diff"]
    last_desktop: str | None = None
    for section in changed_sections:
        desktop, screen, section_lines = section.desktop, section.screen, section.lines
        if desktop != last_desktop:
            lines.append("")
            lines.append(desktop)
            last_desktop = desktop
        lines.append(screen)
        lines.extend(section_lines)
    return "\n".join(lines)


def config_option() -> Path:
    """Placeholder — Typer resolves this via the Option default."""
    from desktop_manager.windows_workspace import default_plan_path

    return default_plan_path()
