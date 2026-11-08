"""Console and plan-path option shared by the workspace commands."""

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
class PlannedProcess:
    desktop_name: str
    entry_name: str
    process_name: str | None


def config_option() -> Path:
    """Placeholder — Typer resolves this via the Option default."""
    from desktop_manager.windows_workspace import default_plan_path

    return default_plan_path()
