"""The session-ledger ``SessionEnd`` hook for Claude Code.

``claude_hooks/session-ledger.mjs`` is copied to ``~/.claude/hooks/`` and wired into
``~/.claude/settings.json`` under ``hooks.SessionEnd``. On every session end it writes
``~/.claude/session-ends/<session_id>.json`` (or ``$CLAUDE_SESSION_LEDGER_DIR``) with
the reason the session ended; ``desktop_manager.claude_sessions.discovery`` reads it.

The workspace repo's devenv installs the same script to the same path and recognises
an existing install by the same rule: any ``SessionEnd`` hook whose script file is
named ``session-ledger.mjs``. Either installer can run first and neither adds a second
entry. The bundled script is kept byte-identical to the workspace copy.

The install and merge logic is shared with the other bundled hooks in :mod:`.bundled_hooks`;
these names are the ledger's view of it.
"""

from __future__ import annotations

from pathlib import Path

from .bundled_hooks import SESSION_LEDGER
from .bundled_hooks import HookState as LedgerHookState

__all__ = [
    "SESSION_LEDGER_MARKER",
    "SESSION_LEDGER_SOURCE",
    "SESSION_LEDGER_TIMEOUT_SECONDS",
    "LedgerHookState",
    "has_session_ledger_hook",
    "hook_script_current",
    "install_hook_script",
    "merge_session_ledger_hook",
    "runs_session_ledger",
    "session_ledger_command",
    "session_ledger_hook_path",
    "session_ledger_state",
]

SESSION_LEDGER_SOURCE = SESSION_LEDGER.source
SESSION_LEDGER_MARKER = SESSION_LEDGER.script
SESSION_LEDGER_TIMEOUT_SECONDS = SESSION_LEDGER.timeout_seconds


def session_ledger_hook_path(home: Path | None = None) -> Path:
    """Where the hook script is installed."""
    return SESSION_LEDGER.hook_path(home)


def session_ledger_command(hook_path: Path) -> str:
    """The hook command line: forward slashes so Git Bash and PowerShell agree."""
    return SESSION_LEDGER.command(hook_path)


def hook_script_current(hook_path: Path) -> bool:
    """True when the installed script matches the bundled one (line endings aside)."""
    return SESSION_LEDGER.script_current(hook_path)


def install_hook_script(hook_path: Path) -> None:
    """Copy the bundled script into place. Raises ``OSError`` when it cannot."""
    SESSION_LEDGER.install(hook_path)


def runs_session_ledger(command: str) -> bool:
    """True when the command executes the ledger script, from any directory."""
    return SESSION_LEDGER.runs(command)


def has_session_ledger_hook(groups: list[object]) -> bool:
    """True when any ``SessionEnd`` hook group already runs the session ledger."""
    return SESSION_LEDGER.wired_in(groups)


def merge_session_ledger_hook(settings: dict[str, object], command: str) -> list[str]:
    """Add the ledger hook to ``settings`` in place unless one is already wired."""
    return SESSION_LEDGER.merge(settings, command)


def session_ledger_state(settings: dict[str, object], hook_path: Path) -> LedgerHookState:
    """Whether the hook is wired into ``settings`` and the installed script is current."""
    return SESSION_LEDGER.state(settings, hook_path)
