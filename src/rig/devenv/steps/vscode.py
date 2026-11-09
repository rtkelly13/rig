"""VS Code user settings: rig's rules merged in, and local backups of the settings folder.

Settings Sync still carries settings between machines. rig adds two things on top:

- **Rules.** ``config/vscode/settings.json`` lists settings every machine must have. They are merged in
  without overwriting: a missing key or object entry is inserted as text, so comments and the user's
  own settings are untouched. A key the user has set to something else is reported as a conflict and
  left alone.
- **Backups.** Before every write, and on demand, the settings folder and the extension list are copied
  to ``<app data>/backups/vscode/<timestamp>/``. The newest ten are kept.
"""

from __future__ import annotations

import json
import os
import shutil
from dataclasses import dataclass
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path

from rich.console import Console

from rig import jsonc
from rig.platforms import Platform
from rig.roots import app_data_dir
from rig.typed_json import decode, is_object, load_json

from ..engine.context import StepContext
from ..providers.base import run_command

console = Console()

DESIRED_PATH = Path(__file__).resolve().parent.parent / "config" / "vscode" / "settings.json"
BACKED_UP_FILES = ("settings.json", "keybindings.json", "mcp.json", "tasks.json")
BACKED_UP_DIRS = ("snippets", "prompts")
MAX_BACKUPS = 10


class ChangeKind(StrEnum):
    ADD = "add"
    CONFLICT = "conflict"


@dataclass(frozen=True, slots=True)
class Change:
    """One difference between the rules and the user's settings.

    ``path`` is the top-level key, then the entry inside it when the rule is an object.
    """

    kind: ChangeKind
    path: tuple[str, ...]
    wanted: object
    current: object = None

    @property
    def label(self) -> str:
        return " → ".join(self.path)


def user_settings_dir(platform: Platform, env: dict[str, str] | None = None, home: Path | None = None) -> Path:
    """The VS Code (stable) ``User`` folder for this platform."""
    env = dict(os.environ) if env is None else env
    home = home or Path.home()
    if platform == Platform.WINDOWS:
        appdata = env.get("APPDATA")
        base = Path(appdata) if appdata else home / "AppData" / "Roaming"
        return base / "Code" / "User"
    if platform == Platform.MACOS:
        return home / "Library" / "Application Support" / "Code" / "User"
    config = env.get("XDG_CONFIG_HOME")
    return (Path(config) if config else home / ".config") / "Code" / "User"


def load_desired(path: Path = DESIRED_PATH) -> dict[str, object]:
    """The rules every machine's VS Code settings must contain."""
    return decode(dict[str, object], load_json(path.read_text(encoding="utf-8")))


def plan(desired: dict[str, object], observed: dict[str, object]) -> list[Change]:
    """What must change for ``observed`` to contain every rule in ``desired``. Pure."""
    changes: list[Change] = []
    for key, wanted in desired.items():
        if key not in observed:
            changes.append(Change(ChangeKind.ADD, (key,), wanted))
            continue
        current = observed[key]
        if is_object(wanted):
            if not is_object(current):
                changes.append(Change(ChangeKind.CONFLICT, (key,), wanted, current))
                continue
            for sub_key, sub_wanted in wanted.items():
                name = str(sub_key)
                if name not in current:
                    changes.append(Change(ChangeKind.ADD, (key, name), sub_wanted))
                elif current[name] != sub_wanted:
                    changes.append(Change(ChangeKind.CONFLICT, (key, name), sub_wanted, current[name]))
        elif current != wanted:
            changes.append(Change(ChangeKind.CONFLICT, (key,), wanted, current))
    return changes


def _indent_unit(text: str, root: int, existing: list[jsonc.Member]) -> str:
    """The indentation the file already uses for top-level keys, else four spaces."""
    if existing:
        start = existing[0].key_start
        line_start = text.rfind("\n", root, start) + 1
        prefix = text[line_start:start]
        if line_start > 0 and prefix and not prefix.strip():
            return prefix
    return "    "


def _render(value: object, unit: str, depth: int) -> str:
    rendered = json.dumps(value, indent=unit, ensure_ascii=False)
    return rendered.replace("\n", "\n" + unit * depth)


def _insertion(entries: list[tuple[str, object]], unit: str, depth: int, *, object_empty: bool) -> str:
    """Text for new ``"key": value`` lines placed straight after an object's ``{``."""
    pad = unit * depth
    lines: list[str] = []
    for index, (key, value) in enumerate(entries):
        last = index == len(entries) - 1
        comma = "" if (last and object_empty) else ","
        lines.append(f"\n{pad}{json.dumps(key, ensure_ascii=False)}: {_render(value, unit, depth)}{comma}")
    if object_empty:
        lines.append("\n" + unit * (depth - 1))
    return "".join(lines)


def apply_text(text: str, changes: list[Change]) -> str:
    """Insert every ADD change into ``text``, leaving the rest of the file as it was."""
    root = jsonc.root_object(text)
    top = jsonc.members(text, root)
    unit = _indent_unit(text, root, top)
    by_key = {member.key: member for member in top}

    top_level: list[tuple[str, object]] = []
    nested: dict[str, list[tuple[str, object]]] = {}
    for change in changes:
        if change.kind != ChangeKind.ADD:
            continue
        if len(change.path) == 1:
            top_level.append((change.path[0], change.wanted))
        else:
            nested.setdefault(change.path[0], []).append((change.path[1], change.wanted))

    inserts: list[tuple[int, str]] = []
    if top_level:
        inserts.append((root + 1, _insertion(top_level, unit, 1, object_empty=not top)))
    for key, entries in nested.items():
        member = by_key[key]
        inner = jsonc.members(text, member.value_start)
        inserts.append((member.value_start + 1, _insertion(entries, unit, 2, object_empty=not inner)))

    for offset, snippet in sorted(inserts, key=lambda item: item[0], reverse=True):
        text = text[:offset] + snippet + text[offset:]
    return text


def read_settings(path: Path) -> tuple[str, dict[str, object]]:
    """The settings file's text and its parsed values. A missing file reads as ``{}``."""
    text = path.read_text(encoding="utf-8") if path.is_file() else "{}\n"
    return text, parse_settings(text)


def parse_settings(text: str) -> dict[str, object]:
    parsed = jsonc.loads(text)
    if not is_object(parsed):
        raise jsonc.JsoncError("settings are not a JSON object")
    return {str(key): value for key, value in parsed.items()}


def default_backup_root() -> Path:
    return app_data_dir() / "backups" / "vscode"


def list_extensions() -> str | None:
    """``code --list-extensions --show-versions``, or ``None`` when the CLI is unavailable."""
    code = shutil.which("code")
    if code is None:
        return None
    try:
        result = run_command([code, "--list-extensions", "--show-versions"], timeout=60)
    except (OSError, TimeoutError):
        return None
    lines = [line for line in result.stdout.splitlines() if "@" in line]
    return "\n".join(lines) + "\n" if result.returncode == 0 and lines else None


def backup(
    user_dir: Path,
    root: Path,
    *,
    extensions: str | None,
    now: datetime | None = None,
    keep: int = MAX_BACKUPS,
) -> Path:
    """Copy the settings folder's files (and the extension list) into a new timestamped folder."""
    stamp = (now or datetime.now(UTC)).strftime("%Y%m%dT%H%M%S%fZ")
    target = root / stamp
    target.mkdir(parents=True, exist_ok=False)
    for name in BACKED_UP_FILES:
        source = user_dir / name
        if source.is_file():
            shutil.copy2(source, target / name)
    for name in BACKED_UP_DIRS:
        source = user_dir / name
        if source.is_dir():
            shutil.copytree(source, target / name)
    if extensions is not None:
        (target / "extensions.txt").write_text(extensions, encoding="utf-8")

    snapshots = sorted(p for p in root.iterdir() if p.is_dir())
    for old in snapshots[: max(0, len(snapshots) - keep)]:
        shutil.rmtree(old, ignore_errors=True)
    return target


def _write_atomic(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    staging.write_text(text, encoding="utf-8", newline="")
    staging.replace(path)


def _report(changes: list[Change]) -> None:
    for change in changes:
        if change.kind == ChangeKind.ADD:
            console.print(f"  [green]+[/green] {change.label} = {json.dumps(change.wanted)}")
        else:
            console.print(
                f"  [yellow]![/yellow] {change.label}: is {json.dumps(change.current)}, "
                f"rule wants {json.dumps(change.wanted)} (left as is)"
            )


def sync_settings(
    ctx: StepContext,
    *,
    user_dir: Path | None = None,
    backup_root: Path | None = None,
    desired: dict[str, object] | None = None,
    extensions: str | None = None,
) -> list[Change]:
    """Merge the rules into the user settings, backing up first. Returns the plan it found."""
    user_dir = user_dir or user_settings_dir(ctx.platform)
    settings = user_dir / "settings.json"
    if not user_dir.is_dir():
        console.print(f"[dim]VS Code settings folder not found ({user_dir.as_posix()}). Skipping.[/dim]")
        return []

    rules = desired if desired is not None else load_desired()
    text, observed = read_settings(settings)
    changes = plan(rules, observed)
    adds = [c for c in changes if c.kind == ChangeKind.ADD]
    if not changes:
        console.print("[green]✓ VS Code settings already follow rig's rules.[/green]")
        return changes

    if ctx.dry_run or not adds:
        console.print(f"[dim]{'Dry run: ' if ctx.dry_run else ''}{settings.as_posix()}[/dim]")
        _report(changes)
        return changes

    new_text = apply_text(text, adds)
    leftover = [c for c in plan(rules, parse_settings(new_text)) if c.kind == ChangeKind.ADD]
    if leftover:
        raise jsonc.JsoncError(f"merged settings still miss {[c.label for c in leftover]}; nothing written")

    snapshot = backup(user_dir, backup_root or default_backup_root(), extensions=extensions)
    _write_atomic(settings, new_text)
    console.print(f"[bold green]✓ Updated {settings.as_posix()}[/bold green] (backup: {snapshot.as_posix()})")
    _report(changes)
    return changes


def run_vscode_setup(ctx: StepContext) -> None:
    """devenv step: apply rig's VS Code rules."""
    try:
        sync_settings(ctx, extensions=None if ctx.dry_run else list_extensions())
    except (OSError, jsonc.JsoncError) as exc:
        console.print(f"[yellow]! Could not update VS Code settings: {exc}[/yellow]")
