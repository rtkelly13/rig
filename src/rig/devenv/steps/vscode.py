"""VS Code user settings: rig's rules merged in, and local backups of the settings folder.

Settings Sync still carries settings between machines. rig adds two things on top:

- **Rules.** ``config/vscode/settings.jsonc`` lists settings every machine must have, each with the
  comment that explains it. They are merged in without overwriting: a missing key, object entry or
  comment is inserted as text, so the user's own settings and comments are untouched. A key the user
  has set to something else is reported as a conflict and left alone.
- **Backups.** Before every write, and on demand, the settings folder and the extension list are copied
  to ``<app data>/backups/vscode/<timestamp>/``. The newest ten are kept.
"""

from __future__ import annotations

import json
import os
import shutil
from collections.abc import Mapping
from dataclasses import dataclass, field
from datetime import UTC, datetime
from enum import StrEnum
from pathlib import Path

from rich.console import Console

from rig import jsonc
from rig.platforms import Platform
from rig.roots import app_data_dir
from rig.typed_json import is_object

from ..engine.context import StepContext
from ..providers.base import run_command

console = Console()

DESIRED_PATH = Path(__file__).resolve().parent.parent / "config" / "vscode" / "settings.jsonc"
BACKED_UP_FILES = ("settings.json", "keybindings.json", "mcp.json", "tasks.json")
BACKED_UP_DIRS = ("snippets", "prompts")
MAX_BACKUPS = 10


class ChangeKind(StrEnum):
    ADD = "add"
    CONFLICT = "conflict"
    COMMENT = "comment"


@dataclass(frozen=True, slots=True)
class Change:
    """One difference between the rules and the user's settings.

    ``path`` is the top-level key, then the entry inside it when the rule is an object. ``comment`` is
    the rule's comment: written above an added key, or the whole change for a ``COMMENT``.
    """

    kind: ChangeKind
    path: tuple[str, ...]
    wanted: object
    current: object = None
    comment: tuple[str, ...] = ()

    @property
    def label(self) -> str:
        return " → ".join(self.path)


@dataclass(frozen=True, slots=True)
class ConfigEnv:
    """The environment variables that move VS Code's ``User`` folder."""

    appdata: str | None = None
    xdg_config_home: str | None = None

    @classmethod
    def from_os(cls) -> ConfigEnv:
        return cls(appdata=os.environ.get("APPDATA"), xdg_config_home=os.environ.get("XDG_CONFIG_HOME"))


def user_settings_dir(platform: Platform, env: ConfigEnv | None = None, home: Path | None = None) -> Path:
    """The VS Code (stable) ``User`` folder for this platform."""
    env = ConfigEnv.from_os() if env is None else env
    home = home or Path.home()
    if platform == Platform.WINDOWS:
        base = Path(env.appdata) if env.appdata else home / "AppData" / "Roaming"
        return base / "Code" / "User"
    if platform == Platform.MACOS:
        return home / "Library" / "Application Support" / "Code" / "User"
    config = Path(env.xdg_config_home) if env.xdg_config_home else home / ".config"
    return config / "Code" / "User"


@dataclass(frozen=True, slots=True)
class Rules:
    """Settings every machine must have, and the comment lines that go above each top-level key."""

    values: dict[str, object]
    comments: dict[str, tuple[str, ...]] = field(default_factory=dict[str, tuple[str, ...]])


def load_desired(path: Path = DESIRED_PATH) -> Rules:
    """The rules every machine's VS Code settings must contain."""
    text = path.read_text(encoding="utf-8")
    return Rules(parse_settings(text), comments_by_key(text))


def comments_by_key(text: str) -> dict[str, tuple[str, ...]]:
    """The comment lines directly above each top-level key that has any."""
    found: dict[str, tuple[str, ...]] = {}
    for member in jsonc.members(text, jsonc.root_object(text)):
        lines = jsonc.comments_above(text, member.key_start)
        if lines:
            found[member.key] = tuple(lines)
    return found


def _has_run(block: tuple[str, ...], lines: tuple[str, ...]) -> bool:
    return any(block[i : i + len(lines)] == lines for i in range(len(block) - len(lines) + 1))


def plan(
    desired: dict[str, object],
    observed: dict[str, object],
    *,
    comments: Mapping[str, tuple[str, ...]] | None = None,
    observed_comments: Mapping[str, tuple[str, ...]] | None = None,
) -> list[Change]:
    """What must change for ``observed`` to contain every rule in ``desired``. Pure.

    A rule's comment counts as present when its lines appear, together and in order, among the comment
    lines above the key; anything else written there stays. A key whose whole value conflicts gets no
    comment: the user has decided otherwise.
    """
    comments = comments or {}
    observed_comments = observed_comments or {}
    changes: list[Change] = []
    for key, wanted in desired.items():
        comment = comments.get(key, ())
        if key not in observed:
            changes.append(Change(ChangeKind.ADD, (key,), wanted, comment=comment))
            continue
        current = observed[key]
        if is_object(wanted) != is_object(current) or (not is_object(wanted) and current != wanted):
            changes.append(Change(ChangeKind.CONFLICT, (key,), wanted, current))
            continue
        if is_object(wanted) and is_object(current):
            for sub_key, sub_wanted in wanted.items():
                name = str(sub_key)
                if name not in current:
                    changes.append(Change(ChangeKind.ADD, (key, name), sub_wanted))
                elif current[name] != sub_wanted:
                    changes.append(Change(ChangeKind.CONFLICT, (key, name), sub_wanted, current[name]))
        if comment and not _has_run(observed_comments.get(key, ()), comment):
            changes.append(Change(ChangeKind.COMMENT, (key,), wanted, comment=comment))
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


@dataclass(frozen=True, slots=True)
class _Entry:
    """A ``"key": value`` line to add, with the comment lines that go above it."""

    key: str
    value: object
    comment: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class _Insert:
    offset: int
    text: str


def _insertion(entries: list[_Entry], unit: str, depth: int, *, object_empty: bool) -> str:
    """Text for new ``"key": value`` lines, each under its comment, placed straight after an object's ``{``."""
    pad = unit * depth
    lines: list[str] = []
    for index, entry in enumerate(entries):
        last = index == len(entries) - 1
        comma = "" if (last and object_empty) else ","
        rendered = _render(entry.value, unit, depth)
        lines.extend(f"\n{pad}// {line}" for line in entry.comment)
        lines.append(f"\n{pad}{json.dumps(entry.key, ensure_ascii=False)}: {rendered}{comma}")
    if object_empty:
        lines.append("\n" + unit * (depth - 1))
    return "".join(lines)


def _comment_insertion(text: str, key_start: int, comment: tuple[str, ...], unit: str) -> str:
    """Text placed at ``key_start`` that puts ``comment`` on the lines above the key."""
    line_start = text.rfind("\n", 0, key_start) + 1
    indent = text[line_start:key_start]
    if indent.strip():
        return "".join(f"\n{unit}// {line}" for line in comment) + "\n" + unit
    return "".join(f"// {line}\n{indent}" for line in comment)


def apply_text(text: str, changes: list[Change]) -> str:
    """Insert every ADD and COMMENT change into ``text``, leaving the rest of the file as it was."""
    root = jsonc.root_object(text)
    top = jsonc.members(text, root)
    unit = _indent_unit(text, root, top)
    by_key = {member.key: member for member in top}

    top_level: list[_Entry] = []
    nested: dict[str, list[_Entry]] = {}
    inserts: list[_Insert] = []
    for change in changes:
        if change.kind == ChangeKind.COMMENT:
            key_start = by_key[change.path[0]].key_start
            inserts.append(_Insert(key_start, _comment_insertion(text, key_start, change.comment, unit)))
        elif change.kind != ChangeKind.ADD:
            continue
        elif len(change.path) == 1:
            top_level.append(_Entry(change.path[0], change.wanted, change.comment))
        else:
            nested.setdefault(change.path[0], []).append(_Entry(change.path[1], change.wanted))

    if top_level:
        inserts.append(_Insert(root + 1, _insertion(top_level, unit, 1, object_empty=not top)))
    for key, entries in nested.items():
        member = by_key[key]
        inner = jsonc.members(text, member.value_start)
        inserts.append(_Insert(member.value_start + 1, _insertion(entries, unit, 2, object_empty=not inner)))

    # Stable sort: at one offset, a comment for the first key lands after keys added in front of it.
    for insert in sorted(inserts, key=lambda item: item.offset, reverse=True):
        text = text[: insert.offset] + insert.text + text[insert.offset :]
    return text


def read_text(path: Path) -> str:
    """The settings file's text. A missing file reads as ``{}``."""
    return path.read_text(encoding="utf-8") if path.is_file() else "{}\n"


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


_WRITES = frozenset({ChangeKind.ADD, ChangeKind.COMMENT})


def _write_atomic(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    staging = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    staging.write_text(text, encoding="utf-8", newline="")
    staging.replace(path)


def _report(changes: list[Change]) -> None:
    for change in changes:
        if change.kind == ChangeKind.ADD:
            console.print(f"  [green]+[/green] {change.label} = {json.dumps(change.wanted)}")
        elif change.kind == ChangeKind.COMMENT:
            console.print(f"  [cyan]#[/cyan] {change.label}: comment // {' '.join(change.comment)}")
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
    desired: Rules | None = None,
    extensions: str | None = None,
) -> list[Change]:
    """Merge the rules into the user settings, backing up first. Returns the plan it found."""
    user_dir = user_dir or user_settings_dir(ctx.platform)
    settings = user_dir / "settings.json"
    if not user_dir.is_dir():
        console.print(f"[dim]VS Code settings folder not found ({user_dir.as_posix()}). Skipping.[/dim]")
        return []

    rules = desired if desired is not None else load_desired()
    text = read_text(settings)
    observed = parse_settings(text)
    changes = plan(rules.values, observed, comments=rules.comments, observed_comments=comments_by_key(text))
    adds = [c for c in changes if c.kind in _WRITES]
    if not changes:
        console.print("[green]✓ VS Code settings already follow rig's rules.[/green]")
        return changes

    if ctx.dry_run or not adds:
        console.print(f"[dim]{'Dry run: ' if ctx.dry_run else ''}{settings.as_posix()}[/dim]")
        _report(changes)
        return changes

    new_text = apply_text(text, adds)
    after = plan(
        rules.values, parse_settings(new_text), comments=rules.comments, observed_comments=comments_by_key(new_text)
    )
    leftover = [c for c in after if c.kind in _WRITES]
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
