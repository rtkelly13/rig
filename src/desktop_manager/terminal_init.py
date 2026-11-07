"""Background upgrades of terminal CLIs (opencode, claude, copilot) before their tabs launch."""

from __future__ import annotations

import contextvars
import os
import shlex
import subprocess
import sys
import tempfile
import threading
import time
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from desktop_manager.executable_paths import resolve_executable_on_path
from desktop_manager.models import (
    LaunchEntry,
    WorkspacePlan,
)
from rig import tracing


@dataclass(frozen=True, slots=True)
class TerminalCommandInitializer:
    command_names: tuple[str, ...]
    lock_path: Path
    resolve_update_command: Callable[[], list[str] | None]


_TERMINAL_COMMAND_INIT_RESULTS: dict[str, bool] = {}
_TERMINAL_COMMAND_INIT_THREADS: dict[str, threading.Thread] = {}
_TERMINAL_COMMAND_INIT_STATE_LOCK = threading.Lock()


# Upper bound on how long a terminal launch will wait for its background upgrade
# to finish. Slightly above the upgrade subprocess timeout (120s) so a normal
# completion always wins the race; if the upgrade overruns this, the terminal
# launches against the current version rather than stalling logon.
_TERMINAL_COMMAND_UPGRADE_WAIT_SECONDS = 180.0
_OPENCODE_INIT_LOCK_PATH = Path(tempfile.gettempdir()) / "desktop-manager-opencode-init.lock"
_CLAUDE_INIT_LOCK_PATH = Path(tempfile.gettempdir()) / "desktop-manager-claude-init.lock"
_COPILOT_INIT_LOCK_PATH = Path(tempfile.gettempdir()) / "desktop-manager-copilot-init.lock"


def start_terminal_command_upgrades_for_plan(plan: WorkspacePlan) -> None:
    """Kick off CLI self-updates for the plan's terminals in the background.

    Each tool's ``upgrade``/``update`` command is slow (``winget upgrade`` for
    Claude Code alone can take ~40s) and previously ran synchronously right
    before the matching terminal launched, serialising ~50s onto the logon
    critical path. Starting them all in parallel up front means that by the time
    each terminal is launched its upgrade is usually already finished, so the
    pre-launch wait is cheap or free. The launch path still waits (see
    :func:`ensure_terminal_command_initialized`) so a terminal never starts
    against a half-updated binary.
    """
    seen: set[str] = set()
    for desktop in plan.desktops:
        for entry in desktop.launch:
            if entry.disabled:
                continue
            initializer = terminal_command_initializer_for_entry(entry)
            if initializer is None:
                continue
            command_name = initializer.command_names[0]
            if command_name in seen:
                continue
            seen.add(command_name)
            _start_terminal_command_initializer(initializer)


def _start_terminal_command_initializer(initializer: TerminalCommandInitializer) -> None:
    command_name = initializer.command_names[0]
    with _TERMINAL_COMMAND_INIT_STATE_LOCK:
        if command_name in _TERMINAL_COMMAND_INIT_RESULTS:
            return
        if command_name in _TERMINAL_COMMAND_INIT_THREADS:
            return
        thread = threading.Thread(
            target=contextvars.copy_context().run,
            args=(_ensure_terminal_command_initializer, initializer),
            name=f"terminal-upgrade-{command_name}",
            daemon=True,
        )
        _TERMINAL_COMMAND_INIT_THREADS[command_name] = thread
        thread.start()


def ensure_terminal_command_initialized(entry: LaunchEntry) -> None:
    initializer = terminal_command_initializer_for_entry(entry)
    if initializer is None:
        return
    command_name = initializer.command_names[0]

    # If the upgrade was kicked off in the background up front, wait for it now --
    # right before we launch the tool -- rather than launching a half-updated
    # binary. The wait is bounded so a hung upgrade cannot stall logon forever.
    with _TERMINAL_COMMAND_INIT_STATE_LOCK:
        upgrade_thread = _TERMINAL_COMMAND_INIT_THREADS.get(command_name)
    if upgrade_thread is not None:
        upgrade_thread.join(timeout=_TERMINAL_COMMAND_UPGRADE_WAIT_SECONDS)
        return

    _ensure_terminal_command_initializer(initializer)


def terminal_command_initializer_for_entry(entry: LaunchEntry) -> TerminalCommandInitializer | None:
    if entry.type != "terminal":
        return None
    command_name = terminal_command_name(entry.command)
    if command_name is None:
        return None
    for initializer in _terminal_command_initializers():
        if command_name in initializer.command_names:
            return initializer
    return None


def _terminal_command_initializers() -> tuple[TerminalCommandInitializer, ...]:
    return (
        TerminalCommandInitializer(
            command_names=("opencode",),
            lock_path=_OPENCODE_INIT_LOCK_PATH,
            resolve_update_command=_resolve_opencode_update_command,
        ),
        TerminalCommandInitializer(
            command_names=("claude", "claude.exe"),
            lock_path=_CLAUDE_INIT_LOCK_PATH,
            resolve_update_command=_resolve_claude_update_command,
        ),
        TerminalCommandInitializer(
            command_names=("copilot", "copilot.exe"),
            lock_path=_COPILOT_INIT_LOCK_PATH,
            resolve_update_command=lambda: _resolve_path_update_command("copilot", "update"),
        ),
    )


def terminal_command_name(command: str | None) -> str | None:
    if command is None:
        return None
    stripped = command.strip()
    if not stripped:
        return None
    try:
        parts = shlex.split(stripped, posix=False)
    except ValueError:
        parts = stripped.split(maxsplit=1)
    if not parts:
        return None
    return Path(parts[0].strip("\"'")).name.casefold()


def _ensure_terminal_command_initializer(initializer: TerminalCommandInitializer) -> None:
    command_name = initializer.command_names[0]

    if _TERMINAL_COMMAND_INIT_RESULTS.get(command_name, False):
        return

    lock_handle = None
    try:
        initializer.lock_path.parent.mkdir(parents=True, exist_ok=True)
        lock_handle = initializer.lock_path.open("a+b")
        if lock_handle.tell() == 0:
            lock_handle.write(b"0")
            lock_handle.flush()
        lock_handle.seek(0)

        if not _CAN_LOCK:
            _TERMINAL_COMMAND_INIT_RESULTS[command_name] = _run_terminal_command_initializer(initializer)
            return

        while True:
            try:
                _lock_first_byte(lock_handle.fileno(), unlock=False)
                break
            except OSError:
                if _TERMINAL_COMMAND_INIT_RESULTS.get(command_name, False):
                    return
                time.sleep(0.1)

        if _TERMINAL_COMMAND_INIT_RESULTS.get(command_name, False):
            return

        _TERMINAL_COMMAND_INIT_RESULTS[command_name] = _run_terminal_command_initializer(initializer)
    finally:
        if lock_handle is not None:
            try:
                if _CAN_LOCK:
                    lock_handle.seek(0)
                    _lock_first_byte(lock_handle.fileno(), unlock=True)
            except OSError:
                pass
            lock_handle.close()


_CAN_LOCK = sys.platform == "win32"


def _lock_first_byte(fd: int, *, unlock: bool) -> None:
    """Lock or unlock the first byte of ``fd`` with msvcrt; a no-op off Windows."""
    if sys.platform == "win32":
        import msvcrt

        msvcrt.locking(fd, msvcrt.LK_UNLCK if unlock else msvcrt.LK_LOCK, 1)


def _run_terminal_command_initializer(initializer: TerminalCommandInitializer) -> bool:
    update_command = initializer.resolve_update_command()
    if update_command is None:
        return False

    try:
        result = tracing.run(
            update_command,
            check=False,
            capture_output=True,
            text=True,
            timeout=120,
            env=_terminal_command_init_environment(),
        )
    except (OSError, subprocess.SubprocessError):
        return False
    return result.returncode == 0


def _resolve_opencode_update_command() -> list[str] | None:
    baseline_binary = _resolve_opencode_baseline_binary()
    if baseline_binary is None:
        executable = resolve_executable_on_path("opencode.cmd") or resolve_executable_on_path("opencode")
        if executable is None:
            return None
        return [executable, "upgrade"]
    return [baseline_binary, "upgrade"]


def _resolve_opencode_baseline_binary() -> str | None:
    opencode_cmd = resolve_executable_on_path("opencode.cmd")
    if opencode_cmd is None:
        opencode_cmd = resolve_executable_on_path("opencode")
    if opencode_cmd is None:
        return None

    install_dir = Path(opencode_cmd).resolve().parent
    baseline_binary = (
        install_dir
        / "node_modules"
        / "opencode-ai"
        / "node_modules"
        / "opencode-windows-x64-baseline"
        / "bin"
        / "opencode.exe"
    )
    if baseline_binary.exists():
        return str(baseline_binary)
    return None


def _resolve_claude_update_command() -> list[str] | None:
    """Run Claude Code's own updater, mirroring how OpenCode upgrades itself.

    Claude Code is installed from npm (the winget manifest tracks the stable
    channel and lags the updater). Updating up front, before any terminal starts,
    means the sessions' built-in auto-updater has nothing left to do, so it does
    not reinstall the package underneath terminals that are still launching.
    ``where.exe claude`` lists the extensionless npm shell shim first, which
    Windows cannot execute, so resolve the real binary behind ``claude.cmd``.
    """
    claude_cmd = resolve_executable_on_path("claude.cmd")
    if claude_cmd is not None:
        npm_binary = (
            Path(claude_cmd).resolve().parent / "node_modules" / "@anthropic-ai" / "claude-code" / "bin" / "claude.exe"
        )
        if npm_binary.exists():
            return [str(npm_binary), "update"]
    return _resolve_path_update_command("claude.exe", "update")


def _resolve_path_update_command(command_name: str, update_subcommand: str) -> list[str] | None:
    executable = resolve_executable_on_path(command_name)
    if executable is None and not command_name.endswith(".exe"):
        executable = resolve_executable_on_path(f"{command_name}.exe")
    if executable is None:
        return None
    return [executable, update_subcommand]


def _terminal_command_init_environment() -> dict[str, str]:
    return dict(os.environ)
