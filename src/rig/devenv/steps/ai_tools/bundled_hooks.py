"""Claude Code hooks that rig bundles, copies to ``~/.claude/hooks/`` and wires into settings.

Each hook is a Node script in ``claude_hooks/``. An install is recognised by its script's file
name, from any directory, so another installer's copy (the workspace repo installs the session
ledger too) never gets a second entry. Settings are only ever added to: a ``hooks`` value of
another shape is the user's own and is left alone.
"""

from __future__ import annotations

import shlex
from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path

from pydantic import StrictStr

from rig.typed_json import try_decode

_HOOKS_SOURCE_DIR = Path(__file__).parent / "claude_hooks"
_NODE_EXECUTABLES = frozenset({"node", "node.exe"})


class HookState(Enum):
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
class _HookGroup:
    hooks: list[object]


def _canonical(data: bytes) -> bytes:
    """Script bytes with LF endings, so a CRLF checkout does not read as a different script."""
    return data.replace(b"\r\n", b"\n")


def _hooks(settings: dict[str, object]) -> dict[str, object] | None:
    """The ``hooks`` object, or None when the user's settings have another shape there."""
    decoded = try_decode(_SettingsHooks, settings)
    return None if decoded is None else decoded.hooks


@dataclass(frozen=True, slots=True)
class BundledHook:
    """One bundled hook script: which file, which event, and how long Claude Code waits for it."""

    script: str
    event: str
    timeout_seconds: int

    @property
    def source(self) -> Path:
        return _HOOKS_SOURCE_DIR / self.script

    def hook_path(self, home: Path | None = None) -> Path:
        """Where the script is installed."""
        return (home or Path.home()) / ".claude" / "hooks" / self.script

    @staticmethod
    def command(hook_path: Path) -> str:
        """The hook command line: forward slashes so Git Bash and PowerShell agree."""
        return f'node "{hook_path.as_posix()}"'

    def script_current(self, hook_path: Path) -> bool:
        """True when the installed script matches the bundled one (line endings aside)."""
        try:
            return _canonical(hook_path.read_bytes()) == _canonical(self.source.read_bytes())
        except OSError:
            return False

    def install(self, hook_path: Path) -> None:
        """Copy the bundled script into place. Raises ``OSError`` when it cannot."""
        hook_path.parent.mkdir(parents=True, exist_ok=True)
        hook_path.write_bytes(_canonical(self.source.read_bytes()))

    def runs(self, command: str) -> bool:
        """True when the command executes this script, from any directory.

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
        return script.rsplit("/", 1)[-1] == self.script

    def wired_in(self, groups: list[object]) -> bool:
        """True when any hook group for this event already runs the script."""
        for raw_group in groups:
            group = try_decode(_HookGroup, raw_group)
            if group is None:
                continue
            for raw_hook in group.hooks:
                hook = try_decode(_HookCommand, raw_hook)
                if hook is not None and self.runs(hook.command):
                    return True
        return False

    def groups(self, settings: dict[str, object]) -> list[object] | None:
        """The ``hooks.<event>`` list, or None when the user's settings have another shape there."""
        hooks = _hooks(settings)
        if hooks is None:
            return None
        if self.event not in hooks:
            return []
        return try_decode(list[object], hooks[self.event])

    def merge(self, settings: dict[str, object], command: str) -> list[str]:
        """Add the hook to ``settings`` in place unless one is already wired.

        Returns the change lines (empty when nothing changed).
        """
        hooks = _hooks(settings)
        groups = self.groups(settings)
        if hooks is None or groups is None or self.wired_in(groups):
            return []
        entry: dict[str, object] = {"type": "command", "command": command, "timeout": self.timeout_seconds}
        groups.append({"hooks": [entry]})
        hooks[self.event] = groups
        settings["hooks"] = hooks
        return [f"+ hooks.{self.event}: {command}"]

    def state(self, settings: dict[str, object], hook_path: Path) -> HookState:
        """Whether the hook is wired into ``settings`` and the installed script is current."""
        groups = self.groups(settings)
        if groups is None or not self.wired_in(groups):
            return HookState.NOT_WIRED
        if not hook_path.is_file():
            return HookState.SCRIPT_MISSING
        if not self.script_current(hook_path):
            return HookState.SCRIPT_OUTDATED
        return HookState.CURRENT


# SessionEnd hooks share a 1.5s budget unless a hook asks for more; a cold Node
# start on Windows can take most of that.
SESSION_LEDGER = BundledHook(script="session-ledger.mjs", event="SessionEnd", timeout_seconds=5)

# Reading the process table on Windows starts PowerShell, which can take a couple of seconds.
LOW_PRIORITY = BundledHook(script="low-priority.mjs", event="SessionStart", timeout_seconds=10)

BUNDLED_HOOKS: tuple[BundledHook, ...] = (SESSION_LEDGER, LOW_PRIORITY)
