from __future__ import annotations

import os
import re
import traceback
from dataclasses import dataclass, field
from datetime import datetime
from functools import cache
from pathlib import Path
from typing import Annotated

from pydantic import BeforeValidator, Field, TypeAdapter, ValidationError, WrapValidator

from rig.typed_json import Lenient, WrapHandler, is_list, is_object, try_decode
from rig.workspace_config import load_desktop_aliases

PLACEHOLDER_PATTERN = re.compile(r"\{([^{}]+)\}")

PLAN_VERSION = 1
COMMAND_AT_LOGON_DEFERRED = "deferred"
COMMAND_AT_LOGON_VALUES = frozenset({"run", COMMAND_AT_LOGON_DEFERRED})
MACHINE_PLAN_TEMPLATE_PATH = Path(__file__).resolve().parents[2] / "desktop-manager" / "workspace-plan.bundle.json"
SAMPLE_PLAN_TEMPLATE_PATH = Path(__file__).resolve().parents[2] / "desktop-manager" / "sample-workspace.json"
SAMPLE_PLAN_SCREEN_COUNT = 2
#: Placeholders every plan can use, read from the environment variable named, where it is set.
ENVIRONMENT_ALIASES = {
    "localappdata": "LOCALAPPDATA",
    "appdata": "APPDATA",
    "userprofile": "USERPROFILE",
    "programfiles": "ProgramFiles",
}


def environment_aliases() -> dict[str, str]:
    """`ENVIRONMENT_ALIASES` resolved from this process's environment; unset variables are left out."""
    return {name: value for name, variable in ENVIRONMENT_ALIASES.items() if (value := os.environ.get(variable))}


WINDOWS_TERMINAL_PROCESSES = {
    "cmd.exe",
    "conhost.exe",
    "powershell.exe",
    "pwsh.exe",
    "windowsterminal.exe",
    "wt.exe",
}
IGNORED_WINDOW_TITLES = {
    "Program Manager",
}
IGNORED_PROCESS_NAMES = {
    "applicationframehost.exe",
    "lockapp.exe",
    "searchhost.exe",
    "shellexperiencehost.exe",
    "startmenuexperiencehost.exe",
    "textinputhost.exe",
    "widgets.exe",
}


type JsonValue = str | int | float | bool | None | list[JsonValue] | dict[str, JsonValue]
type JsonObject = dict[str, JsonValue]


@cache
def _adapter[T](kind: type[T]) -> TypeAdapter[T]:
    return TypeAdapter(kind)


def _decode[T](kind: type[T], data: object) -> T:
    """Validate parsed plan JSON into *kind*. Raises `pydantic.ValidationError` (a `ValueError`)."""
    return _adapter(kind).validate_python(data)


def _rect_or_none(value: object, handler: WrapHandler) -> object:
    """Bounds may be an object or a ``[left, top, width, height]`` list; anything unreadable is absent."""
    if is_list(value) and len(value) == 4:
        value = dict(zip(("left", "top", "width", "height"), value, strict=True))
    try:
        return handler(value)
    except ValidationError:
        return None


def _feature_flag(value: object) -> bool:
    """JSON booleans are the intended form; strings are accepted so a hand-edited
    plan saying ``"off"`` does not silently read as enabled (every non-empty
    string is truthy in Python)."""
    if isinstance(value, str):
        return value.strip().casefold() in {"1", "on", "true", "yes"}
    return bool(value)


@dataclass(frozen=True, slots=True)
class _EntryKind:
    """Just enough of a raw launch entry to tell a shell-less terminal."""

    type: object = None
    shell: object = None


def _default_terminal_shell(value: object) -> object:
    """A terminal entry with no shell opens in Windows Terminal running pwsh."""
    if not is_object(value):
        return value
    kind = try_decode(_EntryKind, value)
    if kind is not None and kind.type == "terminal" and not kind.shell:
        return {**value, "shell": "wt-pwsh"}
    return value


def _list_or_empty(value: object) -> object:
    """A list field holding anything but a list reads as empty, as hand-edited plans expect."""
    return value if is_list(value) else list[object]()


def _text(value: object) -> object:
    """Plans are hand-edited, so a number where text belongs is read as its digits."""
    return value if value is None else str(value)


def _object_or_none(value: object) -> object:
    """An optional nested object that is not a JSON object is absent."""
    return value if is_object(value) else None


def _dict_or_empty(value: object) -> object:
    return value if is_object(value) else dict[object, object]()


def _ignore_input(_value: object) -> object:
    """Runtime-only state: a plan file must never set it."""
    return []


type Text = Annotated[str, BeforeValidator(_text)]
type OptionalText = Annotated[str | None, BeforeValidator(_text)]
type OptionalInt = Lenient[int]
type OptionalObject[T] = Annotated[T | None, BeforeValidator(_object_or_none)]
type ListOrEmpty[T] = Annotated[list[T], BeforeValidator(_list_or_empty)]
type DictOrEmpty[V] = Annotated[dict[Text, V], BeforeValidator(_dict_or_empty)]
type RuntimeOnly[T] = Annotated[list[T], BeforeValidator(_ignore_input)]
type OptionalRect = Annotated[Rect | None, WrapValidator(_rect_or_none)]
type FeatureFlag = Annotated[bool, BeforeValidator(_feature_flag)]
type LaunchItem = Annotated[LaunchEntry, BeforeValidator(_default_terminal_shell)]


PLAN_ENTRY_NOT_OBJECT = "Workspace plan entries must be JSON objects."


def decode_plan_entry[T](kind: type[T], value: object) -> T:
    """Decode one parsed plan object as *kind*.

    Anything but a JSON object raises ``ValueError`` with a plain message; an object that does not fit
    *kind* raises ``pydantic.ValidationError`` (also a ``ValueError``).
    """
    if not is_object(value):
        raise ValueError(PLAN_ENTRY_NOT_OBJECT)
    return _decode(kind, value)


def _empty_string_list() -> list[str]:
    return []


def _empty_launch_entry_list() -> list[LaunchEntry]:
    return []


def _empty_window_snapshot_list() -> list[WindowSnapshot]:
    return []


def _empty_desktop_plan_list() -> list[DesktopPlan]:
    return []


def _empty_string_map() -> dict[str, str]:
    return {}


def _empty_feature_map() -> dict[str, bool]:
    return {}


@dataclass(slots=True)
class Rect:
    left: int
    top: int
    width: int
    height: int

    @property
    def right(self) -> int:
        return self.left + self.width

    @property
    def bottom(self) -> int:
        return self.top + self.height

    @property
    def center_x(self) -> int:
        return self.left + self.width // 2

    @property
    def center_y(self) -> int:
        return self.top + self.height // 2

    def to_dict(self) -> JsonObject:
        return {
            "left": self.left,
            "top": self.top,
            "width": self.width,
            "height": self.height,
        }

    @classmethod
    def from_dict(cls, data: object) -> Rect:
        return _decode(cls, data)


@dataclass(slots=True)
class DisplayArea:
    screen_number: int
    bounds: Rect


@dataclass(slots=True)
class WindowSnapshot:
    title: Text
    pid: OptionalInt = None
    process_name: OptionalText = None
    exe_path: OptionalText = None
    class_name: OptionalText = None
    screen_number: OptionalInt = None
    inferred_placement: OptionalObject[PlacementSpec] = None
    bounds: OptionalRect = None

    def to_dict(self) -> JsonObject:
        data: JsonObject = {"title": self.title}
        if self.pid is not None:
            data["pid"] = self.pid
        if self.process_name:
            data["process_name"] = self.process_name
        if self.exe_path:
            data["exe_path"] = self.exe_path
        if self.class_name:
            data["class_name"] = self.class_name
        if self.screen_number is not None:
            data["screen_number"] = self.screen_number
        if self.inferred_placement is not None:
            data["inferred_placement"] = self.inferred_placement.to_dict()
        if self.bounds is not None:
            data["bounds"] = self.bounds.to_dict()
        return data

    @classmethod
    def from_dict(cls, data: object) -> WindowSnapshot:
        return _decode(cls, data)


@dataclass(slots=True)
class LiveWindow:
    hwnd: int
    title: str
    pid: int | None = None
    process_name: str | None = None
    exe_path: str | None = None
    class_name: str | None = None
    desktop_number: int | None = None
    screen_number: int | None = None
    inferred_placement: PlacementSpec | None = None
    bounds: Rect | None = None


@dataclass(slots=True)
class PlacementLayout:
    mode: Text
    slot: int = 1
    orientation: OptionalText = None
    #: `columns` only: the narrowest a column may be, which sets how many fit across the screen.
    min_width: OptionalInt = None
    #: `columns` only: how many columns the screen holds this run, set when the plan loads. Never saved.
    count: OptionalInt = None

    def to_dict(self) -> JsonObject:
        data: JsonObject = {"mode": self.mode, "slot": self.slot}
        if self.orientation:
            data["orientation"] = self.orientation
        if self.min_width is not None:
            data["min_width"] = self.min_width
        return data

    @classmethod
    def from_dict(cls, data: object) -> PlacementLayout:
        return _decode(cls, data)


@dataclass(slots=True)
class PlacementSpec:
    screen: int
    layout: PlacementLayout
    subdivide: OptionalObject[PlacementLayout] = None

    def to_dict(self) -> JsonObject:
        data: JsonObject = {"screen": self.screen, "layout": self.layout.to_dict()}
        if self.subdivide:
            data["subdivide"] = self.subdivide.to_dict()
        return data

    @classmethod
    def from_dict(cls, data: object) -> PlacementSpec:
        return _decode(cls, data)


@dataclass(slots=True)
class LaunchEntry:
    name: Text
    type: Text = "app"
    path: OptionalText = None
    args: ListOrEmpty[Text] = field(default_factory=_empty_string_list)
    command: OptionalText = None
    #: "deferred": a logon restore (`workspace restore --defer-logon-commands`) opens a
    #: bare shell and leaves `command` to `claude-sessions startup`. Any other restore
    #: runs `command` as usual. Unset (or "run") always runs it.
    command_at_logon: OptionalText = None
    cwd: OptionalText = None
    shell: OptionalText = None
    delay_ms: int = 900
    placement: OptionalObject[PlacementSpec] = None
    window_title: OptionalText = None
    window_process_name: OptionalText = None
    window_wait_timeout_ms: OptionalInt = None
    #: JSON file holding the `pid` of the process that owns this entry's window; only
    #: that process's window is reused for it. See `desktop_manager.window_pid_file`.
    window_pid_file: OptionalText = None
    single_instance: bool = False
    summary: OptionalText = None
    notes: OptionalText = None
    disabled: bool = False
    feature: OptionalText = None

    @property
    def defers_command_at_logon(self) -> bool:
        return self.command_at_logon == COMMAND_AT_LOGON_DEFERRED

    def to_dict(self) -> JsonObject:
        data: JsonObject = {
            "name": self.name,
            "type": self.type,
            "delay_ms": self.delay_ms,
        }
        if self.disabled:
            data["disabled"] = True
        if self.feature:
            data["feature"] = self.feature
        if self.shell:
            data["shell"] = self.shell
        if self.path:
            data["path"] = self.path
        if self.args:
            data["args"] = list[JsonValue](self.args)
        if self.command:
            data["command"] = self.command
        if self.command_at_logon:
            data["command_at_logon"] = self.command_at_logon
        if self.cwd:
            data["cwd"] = self.cwd
        if self.summary:
            data["summary"] = self.summary
        if self.notes:
            data["notes"] = self.notes
        if self.placement:
            data["placement"] = self.placement.to_dict()
        if self.window_title:
            data["window_title"] = self.window_title
        if self.window_process_name:
            data["window_process_name"] = self.window_process_name
        if self.window_wait_timeout_ms is not None:
            data["window_wait_timeout_ms"] = self.window_wait_timeout_ms
        if self.window_pid_file:
            data["window_pid_file"] = self.window_pid_file
        if self.single_instance:
            data["single_instance"] = True
        return data

    @classmethod
    def from_dict(cls, data: object) -> LaunchEntry:
        return _decode(cls, _default_terminal_shell(data))


@dataclass(slots=True)
class DesktopPlan:
    name: Text
    launch: ListOrEmpty[LaunchItem] = field(default_factory=_empty_launch_entry_list)
    captured_windows: ListOrEmpty[WindowSnapshot] = field(default_factory=_empty_window_snapshot_list)
    disabled: bool = False
    feature: OptionalText = None

    def to_dict(self) -> JsonObject:
        data: JsonObject = {
            "name": self.name,
            "launch": [entry.to_dict() for entry in self.launch],
        }
        if self.disabled:
            data["disabled"] = True
        if self.feature:
            data["feature"] = self.feature
        if self.captured_windows:
            data["captured_windows"] = [window.to_dict() for window in self.captured_windows]
        return data

    def bundle_ref(self, path: str) -> JsonObject:
        """The entry a plan bundle lists for this desktop, whose own file sits at `path`."""
        ref: JsonObject = {"path": path}
        if self.feature:
            ref["feature"] = self.feature
        if self.disabled:
            ref["disabled"] = True
        return ref

    @classmethod
    def from_dict(cls, data: object) -> DesktopPlan:
        return _decode(cls, data)


@dataclass(slots=True)
class WorkspacePlan:
    version: int = PLAN_VERSION
    workspace: OptionalText = None
    personal_workspace: Annotated[OptionalText, Field(alias="personal-workspace")] = None
    switch_delay_ms: int = 650
    desktops: ListOrEmpty[DesktopPlan] = field(default_factory=_empty_desktop_plan_list)
    notes: ListOrEmpty[Text] = field(default_factory=_empty_string_list)
    aliases: DictOrEmpty[Text] = field(default_factory=_empty_string_map)
    features: DictOrEmpty[FeatureFlag] = field(default_factory=_empty_feature_map)
    # Names of desktops a switched-off feature dropped from ``desktops`` this run.
    # Runtime-only (never serialized): restore uses it to remove a leftover virtual
    # desktop of that name, so switching a desktop's feature off retires it.
    retired_desktop_names: RuntimeOnly[str] = field(default_factory=_empty_string_list)

    def to_dict(self) -> JsonObject:
        data: JsonObject = {
            "version": self.version,
            "switch_delay_ms": self.switch_delay_ms,
            "desktops": [desktop.to_dict() for desktop in self.desktops],
        }
        if self.features:
            data["features"] = dict[str, JsonValue](self.features)
        if self.workspace:
            data["workspace"] = self.workspace
        if self.personal_workspace:
            data["personal-workspace"] = self.personal_workspace
        if self.notes:
            data["notes"] = list[JsonValue](self.notes)
        if self.aliases:
            data["aliases"] = dict[str, JsonValue](self.aliases)
        return data

    def bundle_root(self, desktop_refs: list[JsonValue]) -> JsonObject:
        """The root file of a plan bundle: this plan's settings, listing desktops by `DesktopPlan.bundle_ref`."""
        root: JsonObject = {
            "version": self.version,
            "switch_delay_ms": self.switch_delay_ms,
            "bundle": {"desktops": desktop_refs},
        }
        if self.workspace:
            root["workspace"] = self.workspace
        if self.personal_workspace:
            root["personal-workspace"] = self.personal_workspace
        if self.notes:
            root["notes"] = list[JsonValue](self.notes)
        if self.aliases:
            root["aliases"] = dict[str, JsonValue](self.aliases)
        if self.features:
            root["features"] = dict[str, JsonValue](self.features)
        return root

    def substitutions(self) -> dict[str, str]:
        """Return a mapping of placeholder names to their resolved values.

        `ENVIRONMENT_ALIASES` come from this machine's environment, so the tracked plan can say
        `{localappdata}` rather than one user's profile path; a plan alias of the same name wins.
        This machine's `tools.desktop.aliases` win over both. They apply here, at resolution,
        and never enter `aliases`, so saving or exporting a plan cannot write them into it."""
        result = {**environment_aliases(), **self.aliases, **load_desktop_aliases()}
        if self.workspace:
            result["workspace"] = self.workspace
        if self.personal_workspace:
            result["personal-workspace"] = self.personal_workspace
        return _resolve_placeholder_map(result)

    @classmethod
    def from_dict(cls, data: object) -> WorkspacePlan:
        return _decode(cls, data)


@dataclass(slots=True)
class RestoreRunLogger:
    run_log_path: Path
    latest_log_path: Path
    lines: list[str] = field(default_factory=_empty_string_list)

    def log(self, message: str) -> str:
        line = f"[{datetime.now().astimezone().isoformat(timespec='seconds')}] {message}"
        self.run_log_path.parent.mkdir(parents=True, exist_ok=True)
        self.lines.append(line)
        with self.run_log_path.open("a", encoding="utf-8") as handle:
            handle.write(line + "\n")
        self.latest_log_path.write_text("\n".join(self.lines) + "\n", encoding="utf-8")
        return line

    def exception(self, message: str) -> str:
        """Log ``message`` followed by the traceback of the exception being handled.

        For a per-entry barrier that keeps a restore going past an unexpected failure:
        the run log keeps the stack, so the failure is diagnosable afterwards.
        """
        return self.log(f"{message}\n{traceback.format_exc().rstrip()}")


@dataclass(slots=True)
class WindowHandleCollector:
    handles: list[int] = field(default_factory=list)

    def add(self, hwnd: int) -> None:
        self.handles.append(hwnd)


@dataclass(frozen=True, slots=True)
class ProcessDetails:
    """A process's image name and executable path; both ``None`` when Windows will not say."""

    name: str | None = None
    exe_path: str | None = None


@dataclass(frozen=True, slots=True)
class WindowMatch:
    """The window found for a launch entry, if any, and whether it was moved to the target desktop."""

    hwnd: int | None
    moved_to_target_desktop: bool = False


@dataclass(frozen=True, slots=True)
class PlacementOutcome:
    """Whether a launch entry's window was placed, and whether it was moved to the target desktop first."""

    placed: bool
    moved_to_target_desktop: bool = False


def _resolve_placeholder_map(values: dict[str, str]) -> dict[str, str]:
    resolved: dict[str, str] = {}

    def resolve_key(key: str, stack: tuple[str, ...] = ()) -> str:
        if key in resolved:
            return resolved[key]
        raw_value = values.get(key, "")
        if key in stack:
            return raw_value

        def replace(match: re.Match[str]) -> str:
            placeholder = match.group(1)
            if placeholder == key:
                return match.group(0)
            if placeholder not in values:
                return match.group(0)
            return resolve_key(placeholder, (*stack, key))

        value = PLACEHOLDER_PATTERN.sub(replace, raw_value)
        resolved[key] = value
        return value

    for item in values:
        resolve_key(item)
    return resolved
