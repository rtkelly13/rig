"""Starting plan entries as processes, tracking them, and resolving their executables."""

from __future__ import annotations

import base64
import contextlib
import os
import subprocess
import sys
import time
from collections.abc import Generator, Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from desktop_manager.executable_paths import resolve_versioned_path
from desktop_manager.models import (
    LaunchEntry,
)
from desktop_manager.placeholders import substitute_placeholders
from desktop_manager.terminal_init import (
    ensure_terminal_command_initialized,
    terminal_command_initializer_for_entry,
    terminal_command_name,
)
from rig import tracing


class ProcessHandle(Protocol):
    """The one thing cleanup needs from a launched process: whether it is still alive."""

    def poll(self) -> int | None: ...


@dataclass(frozen=True)
class LaunchedProcess:
    """A process ``launch_entry`` started, kept so cleanup can verify before killing."""

    pid: int
    image_name: str
    handle: ProcessHandle | None = None


# Never terminated by cleanup, whatever the PID bookkeeping says. Warp hands a new
# window to its already-running instance and the launcher exits, so a Warp PID we hold
# is exactly the case where the recorded PID and the surviving process have come apart
# — and killing the wrong one takes down the user's terminal and any Claude session in
# it. Windows Terminal behaves the same way.
PROTECTED_IMAGE_NAMES = frozenset({"warp.exe", "windowsterminal.exe", "wt.exe"})


# The cleanup helpers below only work on Windows. Reading the platform through a module
# constant is what lets a test exercise their guard logic elsewhere: monkeypatching
# ``os.name`` itself makes pathlib try to build ``WindowsPath`` objects and takes the
# whole run down with it.
IS_WINDOWS: bool = os.name == "nt"

_LAUNCH_TRACKERS: list[list[LaunchedProcess]] = []


def _record_launched_process(launched: LaunchedProcess) -> int:
    """Record a launch against every active tracker so cleanup mode can reap it."""
    for tracker in _LAUNCH_TRACKERS:
        tracker.append(launched)
    return launched.pid


def process_image_name(pid: int) -> str | None:
    """Return the current image name for ``pid``, or None if it is not running."""
    if not IS_WINDOWS:  # pragma: no cover - restore only runs on Windows
        return None
    result = tracing.run(
        ["tasklist", "/FI", f"PID eq {pid}", "/NH", "/FO", "CSV"],
        capture_output=True,
        text=True,
        check=False,
    )
    first_field = result.stdout.strip().split(",")[0].strip('"')
    return first_field or None if first_field.casefold().endswith(".exe") else None


def terminate_launched_process(launched: LaunchedProcess) -> bool:
    """Kill a process this run started, but only once it is provably still that process.

    Three guards, because ``taskkill /T /F`` on a stale PID is unrecoverable:

    * If we hold the handle and it has already exited, do nothing. A finished launcher's
      PID can be recycled by Windows onto something else entirely — including the user's
      terminal — and killing by number alone would hit whatever inherited it.
    * The image name must still match what we launched, which catches recycling even
      when no handle is available.
    * Protected images are never killed. See :data:`PROTECTED_IMAGE_NAMES`. What those
      launches leak is reaped by :func:`close_idle_terminal_shells` instead, which works
      on the shell inside each window rather than on the shared host process.

    ``/T`` is used because most entries start via a launcher that spawns the real window
    and exits, so the tree is the only way to reach what opened.
    """
    if not IS_WINDOWS:  # pragma: no cover - restore only runs on Windows
        return False
    if launched.handle is not None and launched.handle.poll() is not None:
        return False
    current_image = process_image_name(launched.pid)
    if current_image is None:
        return False
    if current_image.casefold() in PROTECTED_IMAGE_NAMES:
        return False
    if current_image.casefold() != launched.image_name.casefold():
        return False
    result = tracing.run(
        ["taskkill", "/PID", str(launched.pid), "/T", "/F"],
        capture_output=True,
        text=True,
        check=False,
    )
    return result.returncode == 0


def close_idle_terminal_shells(*, created_after: float, images: frozenset[str] | None = None) -> list[int]:
    """Close windows a single-instance terminal host leaked, by killing their shells.

    ``terminate_launched_process`` cannot help here: every window of a Warp or Windows
    Terminal instance belongs to one shared process, so there is no per-window PID to
    kill and killing the host would take down the session that started the restore.

    Each window does own its own shell process though, and a shell with no child process
    has nothing running in it. Reaping only childless shells started after
    ``created_after`` closes exactly the windows that appeared unattended on this run and
    leaves any window with work in flight — an active session included — untouched.

    Returns the PIDs actually terminated.
    """
    if not IS_WINDOWS:  # pragma: no cover - restore only runs on Windows
        return []
    from desktop_manager.processes import idle_shell_pids_under, is_idle_shell_under

    closed: list[int] = []
    for image in sorted(images if images is not None else PROTECTED_IMAGE_NAMES):
        for pid in idle_shell_pids_under(image, created_after=created_after):
            if not is_idle_shell_under(pid, image):
                continue
            result = tracing.run(
                ["taskkill", "/PID", str(pid), "/F"],
                capture_output=True,
                text=True,
                check=False,
            )
            if result.returncode == 0:
                closed.append(pid)
    return closed


@contextlib.contextmanager
def track_launched_processes(
    *, terminate: bool = True, close_leaked_terminal_windows: bool = False
) -> Generator[list[LaunchedProcess]]:
    """Cleanup mode: collect what ``launch_entry`` started and reap it on exit.

    Tests that exercise a real (non-dry-run) restore would otherwise leave a trail of
    terminals and editors behind on the developer's desktop. Wrapping the run yields the
    live launch list and, by default, terminates each process tree on the way out
    subject to the guards in :func:`terminate_launched_process`.

    A launcher that handed its window to an existing instance cannot be reaped by PID.
    Set ``close_leaked_terminal_windows`` to also close idle shells that appeared under a
    single-instance terminal host while the block was open — see
    :func:`close_idle_terminal_shells`. It is off by default because it acts on windows
    this run did not record, so it should be an explicit choice.
    """
    tracker: list[LaunchedProcess] = []
    started_at = time.time()
    _LAUNCH_TRACKERS.append(tracker)
    try:
        yield tracker
    finally:
        _LAUNCH_TRACKERS.remove(tracker)
        if terminate:
            for launched in reversed(tracker):
                with contextlib.suppress(Exception):
                    terminate_launched_process(launched)
        if close_leaked_terminal_windows:
            with contextlib.suppress(Exception):
                close_idle_terminal_shells(created_after=started_at)


# Variables a Claude Code session sets on every child process. When a restore runs
# from inside a session (an agent re-running today's restore), launched windows
# inherit them: on 2026-10-01 all four Workspace Terminals opened Claude with
# NO_COLOR=1 (monochrome) and CLAUDE_CODE_CHILD_SESSION=1 (transcripts off).
_AGENT_SESSION_ENV_NAMES = frozenset({"NO_COLOR", "CLAUDECODE", "CLAUDE_PID"})
_AGENT_SESSION_ENV_PREFIXES = ("CLAUDE_CODE_",)


def clean_launch_environment(environ: Mapping[str, str] | None = None) -> dict[str, str]:
    """Return ``environ`` (default ``os.environ``) without inherited agent-session variables.

    Launched windows should start as if from the logon task, whatever process ran the restore.
    """
    source = os.environ if environ is None else environ
    return {
        name: value
        for name, value in source.items()
        if name.upper() not in _AGENT_SESSION_ENV_NAMES and not name.upper().startswith(_AGENT_SESSION_ENV_PREFIXES)
    }


def launch_entry(entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None = None) -> int | None:
    """Start a launch entry and return the PID of the process that was spawned.

    The PID is propagated so callers (and ``track_launched_processes``) can close what
    a restore opened. It is the PID of the *launcher*, which for several entry types
    is not the process that ends up owning the window — see
    ``terminate_launched_process``.
    """
    with tracing.span(
        "restore.launch_entry",
        scope="desktop_manager.restore",
        **{"entry.name": entry.name, "entry.type": entry.type, "entry.restore_token": restore_token or ""},
    ) as launch_span:
        pid = _launch_entry(entry, substitutions, restore_token=restore_token)
        launch_span.set(**{"process.pid": pid or 0})
        return pid


def _launch_entry(entry: LaunchEntry, substitutions: dict[str, str], *, restore_token: str | None) -> int | None:
    if entry.type == "terminal":
        ensure_terminal_command_initialized(entry)
        command = build_terminal_command(entry, substitutions, restore_token=restore_token)
        process = subprocess.Popen(command, creationflags=_console_creation_flags(), env=clean_launch_environment())
        return _record_launched_process(
            LaunchedProcess(pid=process.pid, image_name=Path(command[0]).name, handle=process)
        )

    if entry.type == "app":
        path = substitute_placeholders(entry.path, substitutions)
        if not path:
            raise ValueError(f"Launch entry `{entry.name}` is missing a path.")
        expanded_path = resolve_versioned_path(path)
        _validate_launch_path_exists(expanded_path, entry_name=entry.name, field_name="path")
        resolved_args = [
            os.path.expandvars(arg)
            for arg in (substitute_placeholders(item, substitutions) for item in entry.args)
            if arg
        ]
        resolved_args = [
            str(Path(arg).expanduser()) if _extract_local_path_arg(arg) is not None else arg for arg in resolved_args
        ]
        for arg in resolved_args:
            local_path_arg = _extract_local_path_arg(arg)
            if local_path_arg is not None:
                _validate_launch_path_exists(local_path_arg, entry_name=entry.name, field_name="arg")
        # Optional working directory for app launches. Some apps (e.g. Warp on
        # Windows, which ignores warp:// launch configs) open their new window in
        # the launching process's cwd, so setting this is the only way to open
        # them in a specific directory.
        app_cwd = substitute_placeholders(entry.cwd, substitutions) if entry.cwd else None
        if app_cwd:
            _validate_launch_path_exists(app_cwd, entry_name=entry.name, field_name="cwd")
        process = subprocess.Popen(
            [expanded_path, *resolved_args],
            cwd=app_cwd or None,
            env=clean_launch_environment(),
            creationflags=detached_creation_flags(),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
        return _record_launched_process(
            LaunchedProcess(pid=process.pid, image_name=Path(expanded_path).name, handle=process)
        )

    command = substitute_placeholders(entry.command, substitutions)
    if not command:
        raise ValueError(f"Launch entry `{entry.name}` is missing a command.")
    cwd = substitute_placeholders(entry.cwd, substitutions)
    if cwd:
        _validate_launch_path_exists(cwd, entry_name=entry.name, field_name="cwd")
    # Pass the whole thing as one string, not ["cmd.exe", "/c", command]. The list
    # form runs it through list2cmdline, which backslash-escapes the command's own
    # double quotes, so cmd.exe received
    #     \"C:\Program Files\Microsoft VS Code\Code.exe\"
    # and answered "is not recognized as an internal or external command". Because
    # stdout/stderr are discarded and cmd.exe still exits, the failure was silent:
    # the `VS Code Workspace` entry never opened a window and every restore reported
    # it missing. A string command line reaches CreateProcess verbatim.
    process = subprocess.Popen(
        f"cmd.exe /c {command}",
        cwd=cwd,
        env=clean_launch_environment(),
        creationflags=detached_creation_flags(),
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    return _record_launched_process(LaunchedProcess(pid=process.pid, image_name="cmd.exe", handle=process))


def build_terminal_command(
    entry: LaunchEntry,
    substitutions: dict[str, str],
    *,
    restore_token: str | None = None,
) -> list[str]:
    cwd = substitute_placeholders(entry.cwd, substitutions)
    command = substitute_placeholders(entry.command, substitutions) or ""
    # A deferred entry restored at logon has its command withheld: it opens a bare shell
    # that `claude-sessions startup` fills (see `plan.defer_logon_commands`).
    bare_shell = not command and entry.defers_command_at_logon and entry.shell == "wt-pwsh"
    if not cwd or not (command or bare_shell):
        raise ValueError(f"Terminal entry `{entry.name}` must define both cwd and command.")
    _validate_launch_path_exists(cwd, entry_name=entry.name, field_name="cwd")

    if command and entry.shell == "wt-pwsh" and terminal_command_initializer_for_entry(entry) is not None:
        command = _powershell_self_updating_launch(command, terminal_command_name(command) or command)

    if restore_token is not None:
        command = (
            f"$env:OPENCODE_RESTORE_TOKEN = {_powershell_single_quote(restore_token)}; "
            f"$env:OPENCODE_RESTORE_ENTRY = {_powershell_single_quote(entry.name)}"
            + (f"; {command}" if command else "")
        )

    if entry.shell == "wt-pwsh":
        wt_executable = find_windows_terminal_executable()
        title_arg = ["--title", entry.window_title] if entry.window_title else []
        command_args = ["-NoExit", "-EncodedCommand", _powershell_encoded_command(command)] if command else []
        return [
            wt_executable,
            "-w",
            "new",
            "new-tab",
            *title_arg,
            "--startingDirectory",
            cwd,
            "pwsh.exe",
            "-NoLogo",
            *command_args,
        ]

    if entry.shell in {"powershell", "pwsh"}:
        shell_executable = "pwsh.exe" if entry.shell == "pwsh" else "powershell.exe"
        title_prefix = f'$Host.UI.RawUI.WindowTitle = "{entry.window_title}"; ' if entry.window_title else ""
        return [
            shell_executable,
            "-NoExit",
            "-Command",
            f'{title_prefix}Set-Location -LiteralPath "{cwd}"; {command}',
        ]

    title_prefix = f"title {entry.window_title} && " if entry.window_title else ""
    return ["cmd.exe", "/k", f'{title_prefix}cd /d "{cwd}" && {command}']


_SELF_UPDATE_LAUNCH_ATTEMPTS = 6
_SELF_UPDATE_READY_WAIT_SECONDS = 90
_SELF_UPDATE_FAST_FAILURE_SECONDS = 20
_SELF_UPDATE_RETRY_DELAY_SECONDS = 5


def _powershell_self_updating_launch(command: str, command_name: str) -> str:
    """Wrap a self-updating CLI launch in a wait-and-retry loop.

    Tools like Claude Code update themselves in the background: an already-running
    session reinstalls the npm package, which deletes and rewrites the ``.exe`` the
    shim points at. A terminal launched during that window gets "command not found"
    (or the shim's "cannot find the path") and drops to a bare prompt. The check
    has to run inside the terminal, at launch time, because the swap can start
    after desktop-manager has already handed the command to Windows Terminal.

    Readiness is naive: the command resolves, and if it resolves to an npm shim,
    the ``.exe`` the shim invokes exists. A launch that fails within
    ``_SELF_UPDATE_FAST_FAILURE_SECONDS`` is treated as a mid-update failure and
    retried; a longer-lived session ending (the user quitting) is not.
    """
    return (
        f"$dmName = {_powershell_single_quote(command_name)}; "
        "function Test-DmReady { "
        "$c = Get-Command $dmName -CommandType Application,ExternalScript -ErrorAction SilentlyContinue "
        "| Select-Object -First 1; "
        "if (-not $c) { return $false }; "
        "if ($c.Source -match '\\.(cmd|ps1)$') { "
        "$m = [regex]::Match((Get-Content -Raw -LiteralPath $c.Source), "
        "'(?:%dp0%|\\$basedir)[\\\\/]([^\"]+?\\.exe)'); "
        "if ($m.Success) { return Test-Path -LiteralPath (Join-Path (Split-Path $c.Source) $m.Groups[1].Value) } }; "
        "return $true }; "
        f"for ($dmAttempt = 1; $dmAttempt -le {_SELF_UPDATE_LAUNCH_ATTEMPTS}; $dmAttempt++) {{ "
        f"$dmDeadline = (Get-Date).AddSeconds({_SELF_UPDATE_READY_WAIT_SECONDS}); "
        "$dmWaited = $false; "
        "while (-not (Test-DmReady) -and (Get-Date) -lt $dmDeadline) { "
        "if (-not $dmWaited) { Write-Host \"desktop-manager: '$dmName' is missing (likely mid-update); waiting...\" "
        "-ForegroundColor Yellow; $dmWaited = $true }; "
        "Start-Sleep -Seconds 2 }; "
        "$dmStarted = Get-Date; $global:LASTEXITCODE = 0; "
        f"& {{ {command} }}; "
        "$dmOk = $? -and ($LASTEXITCODE -eq 0); "
        f"if ($dmOk -or ((Get-Date) - $dmStarted).TotalSeconds -ge {_SELF_UPDATE_FAST_FAILURE_SECONDS}) {{ break }}; "
        f"if ($dmAttempt -lt {_SELF_UPDATE_LAUNCH_ATTEMPTS}) {{ "
        f"Write-Host \"desktop-manager: '$dmName' failed to start (attempt $dmAttempt/{_SELF_UPDATE_LAUNCH_ATTEMPTS}); "
        f'retrying in {_SELF_UPDATE_RETRY_DELAY_SECONDS}s - Ctrl+C to stop" -ForegroundColor Yellow; '
        f"Start-Sleep -Seconds {_SELF_UPDATE_RETRY_DELAY_SECONDS} }} }}"
    )


def find_windows_terminal_executable() -> str:
    local_appdata = os.environ.get("LOCALAPPDATA")
    candidate_paths = [
        *([Path(local_appdata) / "Microsoft" / "WindowsApps" / "wt.exe"] if local_appdata else []),
        Path.home() / "AppData" / "Local" / "Microsoft" / "WindowsApps" / "wt.exe",
    ]
    for candidate in candidate_paths:
        if candidate.exists():
            return str(candidate)
    return "wt.exe"


def _powershell_encoded_command(command: str) -> str:
    encoded_bytes = command.encode("utf-16le")
    return base64.b64encode(encoded_bytes).decode("ascii")


def _powershell_single_quote(value: str) -> str:
    return "'" + value.replace("'", "''") + "'"


def detached_creation_flags() -> int:
    if sys.platform != "win32":
        return 0
    return subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP


def _console_creation_flags() -> int:
    if sys.platform != "win32":
        return 0
    return subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.CREATE_NEW_CONSOLE


def _validate_launch_path_exists(path_value: str, *, entry_name: str, field_name: str) -> None:
    expanded = Path(os.path.expandvars(path_value)).expanduser()
    if expanded.exists():
        return
    raise ValueError(f"Launch entry `{entry_name}` resolved {field_name} does not exist: {path_value}")


def _extract_local_path_arg(arg: str) -> str | None:
    cleaned = arg.strip().strip('"')
    if not cleaned:
        return None
    lowered = cleaned.lower()
    if lowered.startswith(("http://", "https://", "--", "-")):
        return None
    if len(cleaned) >= 3 and cleaned[1] == ":" and cleaned[2] in {"\\", "/"}:
        return cleaned
    if cleaned.startswith(("./", "../", "/", "~/", "~\\")):
        return cleaned
    return None
