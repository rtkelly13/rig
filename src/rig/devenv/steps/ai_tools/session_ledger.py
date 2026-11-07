"""The session-ledger ``SessionEnd`` hook for Claude Code.

``claude_hooks/session-ledger.mjs`` is copied to ``~/.claude/hooks/`` and wired into
``~/.claude/settings.json`` under ``hooks.SessionEnd``. On every session end it writes
``~/.claude/session-ends/<session_id>.json`` (or ``$CLAUDE_SESSION_LEDGER_DIR``) with
the reason the session ended; ``desktop_manager.claude_sessions.discovery`` reads it.

The workspace repo's devenv installs the same script to the same path and recognises
an existing install by the same rule: any ``SessionEnd`` hook whose script file is
named ``session-ledger.mjs``. Either installer can run first and neither adds a second
entry. The bundled script is kept byte-identical to the workspace copy.
"""

from __future__ import annotations

import shlex
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Annotated

from pydantic import Field, StrictStr

from rig.typed_json import try_decode

SESSION_LEDGER_SOURCE = Path(__file__).parent / "claude_hooks" / "session-ledger.mjs"
SESSION_LEDGER_MARKER = "session-ledger.mjs"
# SessionEnd hooks share a 1.5s budget unless a hook asks for more; a cold Node
# start on Windows can take most of that.
SESSION_LEDGER_TIMEOUT_SECONDS = 5

_NODE_EXECUTABLES = frozenset({"node", "node.exe"})


class LedgerHookState(Enum):
    CURRENT = "installed"
    NOT_WIRED = "not wired in settings.json"
    SCRIPT_MISSING = "script missing"
    SCRIPT_OUTDATED = "script out of date"


@dataclass(frozen=True, slots=True)
class _HookCommand:
    command: StrictStr


@dataclass(frozen=True, slots=True)
class _SettingsHooks:
    """The ``hooks`` object of a Claude settings file; absent reads as empty."""

    hooks: dict[str, object] = field(default_factory=dict[str, object])


@dataclass(frozen=True, slots=True)
class _SessionEndHooks:
    """The ``SessionEnd`` list inside ``hooks``; absent reads as empty."""

    session_end: Annotated[list[object], Field(alias="SessionEnd")] = field(default_factory=list[object])


@dataclass(frozen=True, slots=True)
class _HookGroup:
    hooks: list[object]


def session_ledger_hook_path(home: Path | None = None) -> Path:
    """Where the hook script is installed."""
    return (home or Path.home()) / ".claude" / "hooks" / SESSION_LEDGER_MARKER


def session_ledger_command(hook_path: Path) -> str:
    """The hook command line: forward slashes so Git Bash and PowerShell agree."""
    return f'node "{hook_path.as_posix()}"'


def _canonical(data: bytes) -> bytes:
    """Script bytes with LF endings, so a CRLF checkout does not read as a different script."""
    return data.replace(b"\r\n", b"\n")


def hook_script_current(hook_path: Path) -> bool:
    """True when the installed script matches the bundled one (line endings aside)."""
    try:
        return _canonical(hook_path.read_bytes()) == _canonical(SESSION_LEDGER_SOURCE.read_bytes())
    except OSError:
        return False


def install_hook_script(hook_path: Path) -> None:
    """Copy the bundled script into place. Raises ``OSError`` when it cannot."""
    hook_path.parent.mkdir(parents=True, exist_ok=True)
    hook_path.write_bytes(_canonical(SESSION_LEDGER_SOURCE.read_bytes()))


def runs_session_ledger(command: str) -> bool:
    """True when the command executes the ledger script, from any directory.

    Accepts ``node [flags] <path>`` or the script path on its own, quoted or not, with
    Windows or POSIX separators. A command that only passes the file name as an
    argument (``echo session-ledger.mjs``) does not count.
    """
    try:
        tokens = shlex.split(command.replace("\\", "/"))
    except ValueError:
        return False
    if not tokens:
        return False
    if tokens[0].rsplit("/", 1)[-1].lower() in _NODE_EXECUTABLES:
        args = [token for token in tokens[1:] if not token.startswith("-")]
        script = args[0] if args else ""
    else:
        script = tokens[0]
    return script.rsplit("/", 1)[-1] == SESSION_LEDGER_MARKER


def has_session_ledger_hook(groups: list[object]) -> bool:
    """True when any ``SessionEnd`` hook group already runs the session ledger."""
    for raw_group in groups:
        group = try_decode(_HookGroup, raw_group)
        if group is None:
            continue
        for raw_hook in group.hooks:
            hook = try_decode(_HookCommand, raw_hook)
            if hook is not None and runs_session_ledger(hook.command):
                return True
    return False


def _hooks(settings: dict[str, object]) -> dict[str, object] | None:
    """The ``hooks`` object, or None when the user's settings have another shape there."""
    decoded = try_decode(_SettingsHooks, settings)
    return None if decoded is None else decoded.hooks


def _session_end_groups(settings: dict[str, object]) -> list[object] | None:
    """The ``hooks.SessionEnd`` list, or None when the user's settings have another shape there."""
    hooks = _hooks(settings)
    decoded = None if hooks is None else try_decode(_SessionEndHooks, hooks)
    return None if decoded is None else decoded.session_end


def merge_session_ledger_hook(settings: dict[str, object], command: str) -> list[str]:
    """Add the ledger hook to ``settings`` in place unless one is already wired.

    A ``hooks`` value that isn't an object, or a ``SessionEnd`` value that isn't a
    list, is the user's own shape: it is left alone rather than overwritten. Returns
    the change lines (empty when nothing changed).
    """
    hooks = _hooks(settings)
    groups = _session_end_groups(settings)
    if hooks is None or groups is None or has_session_ledger_hook(groups):
        return []
    entry: dict[str, object] = {"type": "command", "command": command, "timeout": SESSION_LEDGER_TIMEOUT_SECONDS}
    groups.append({"hooks": [entry]})
    hooks["SessionEnd"] = groups
    settings["hooks"] = hooks
    return [f"+ hooks.SessionEnd: {command}"]


def session_ledger_state(settings: dict[str, object], hook_path: Path) -> LedgerHookState:
    """Whether the hook is wired into ``settings`` and the installed script is current."""
    groups = _session_end_groups(settings)
    if groups is None or not has_session_ledger_hook(groups):
        return LedgerHookState.NOT_WIRED
    if not hook_path.is_file():
        return LedgerHookState.SCRIPT_MISSING
    if not hook_script_current(hook_path):
        return LedgerHookState.SCRIPT_OUTDATED
    return LedgerHookState.CURRENT
