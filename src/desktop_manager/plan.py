from __future__ import annotations

import json
import logging
import os
import re
import sqlite3
import sys
from collections.abc import Sequence
from dataclasses import dataclass, field, replace
from datetime import datetime
from pathlib import Path
from typing import Annotated

from pydantic import BeforeValidator, Field, StrictStr

from desktop_manager.models import (
    MACHINE_PLAN_TEMPLATE_PATH,
    PLAN_ENTRY_NOT_OBJECT,
    PLAN_VERSION,
    SAMPLE_PLAN_SCREEN_COUNT,
    SAMPLE_PLAN_TEMPLATE_PATH,
    DesktopPlan,
    DictOrEmpty,
    FeatureFlag,
    JsonValue,
    LaunchEntry,
    LaunchItem,
    ListOrEmpty,
    OptionalText,
    RestoreRunLogger,
    Text,
    WindowSnapshot,
    WorkspacePlan,
    decode_plan_entry,
)
from desktop_manager.placement import (
    fit_columns,
    live_screen_widths,
    placement_spec,
    validate_plan,
    validate_plan_structure,
)
from rig.flags import FLAGS, FlagState, flag_states
from rig.typed_json import Lenient, decode, is_object, load_json, try_decode


def default_plan_path() -> Path:
    if sys.platform == "win32":
        appdata = os.environ.get("APPDATA")
        if appdata:
            return Path(appdata) / "workspace-manager" / "windows-workspace.json"
    return Path.home() / ".config" / "workspace-manager" / "windows-workspace.json"


LOG_DIR_ENV = "DESKTOP_MANAGER_LOG_DIR"


def workspace_logs_dir(config_path: Path) -> Path:
    """`logs` next to the plan, unless `DESKTOP_MANAGER_LOG_DIR` is set (the scheduled logon run sets it)."""
    override = os.environ.get(LOG_DIR_ENV)
    return Path(override) if override else config_path.parent / "logs"


@dataclass(frozen=True, slots=True)
class RestoreLogPaths:
    """Where one restore run logs: its own timestamped file, and the file that always holds the latest run."""

    run: Path
    latest: Path


def restore_log_paths(config_path: Path, now: datetime | None = None) -> RestoreLogPaths:
    timestamp = (now or datetime.now().astimezone()).strftime("%Y%m%d-%H%M%S")
    log_dir = workspace_logs_dir(config_path)
    return RestoreLogPaths(log_dir / f"workspace-restore-{timestamp}.log", log_dir / "workspace-restore-latest.log")


def create_restore_run_logger(config_path: Path, now: datetime | None = None) -> RestoreRunLogger:
    paths = restore_log_paths(config_path, now=now)
    return RestoreRunLogger(run_log_path=paths.run, latest_log_path=paths.latest)


class UnknownFeatureError(ValueError):
    """Raised when a feature toggle names a feature the plan does not declare.

    A silent no-op would be the worst outcome here: the caller asked for a
    different workspace and would get the current one with no indication the
    flag was ignored.
    """


def resolve_feature_flags(
    plan: WorkspacePlan,
    *,
    enable: Sequence[str] = (),
    disable: Sequence[str] = (),
) -> dict[str, bool]:
    """Return each declared feature's effective on/off state.

    Plan-level ``features`` supplies the defaults; ``enable``/``disable`` override
    them. Naming the same feature in both is a conflict rather than a
    last-one-wins guess.
    """
    resolved = dict(plan.features)
    requested = [*enable, *disable]
    unknown = sorted({name for name in requested if name not in resolved})
    if unknown:
        known = ", ".join(sorted(resolved)) or "<none declared>"
        raise UnknownFeatureError(f"Unknown feature(s): {', '.join(unknown)}. Declared features: {known}")
    conflicting = sorted(set(enable) & set(disable))
    if conflicting:
        raise UnknownFeatureError(f"Feature(s) both enabled and disabled: {', '.join(conflicting)}")
    for name in enable:
        resolved[name] = True
    for name in disable:
        resolved[name] = False
    return resolved


def _desktop_switched_off(desktop: DesktopPlan, resolved: dict[str, bool]) -> bool:
    """Whether ``desktop`` should be absent from this run."""
    if desktop.disabled:
        return True
    return desktop.feature is not None and not resolved.get(desktop.feature, False)


def apply_feature_flags(
    plan: WorkspacePlan,
    *,
    enable: Sequence[str] = (),
    disable: Sequence[str] = (),
) -> WorkspacePlan:
    """Return a copy of ``plan`` with entries behind switched-off features disabled.

    Feature state is collapsed onto the existing per-entry ``disabled`` flag, which
    every consumer (restore, placement, validation, visualization) already honors,
    so a paused feature is indistinguishable from a hand-disabled entry downstream.
    An entry disabled in the plan file stays disabled whatever its feature says.

    A whole desktop can sit behind a feature too. A switched-off desktop is dropped
    from ``desktops`` rather than emptied, because the desktop count is the length of
    that list: leaving a husk behind would still create the virtual desktop and shift
    every later desktop's position. Dropping it means the desktop genuinely does not
    exist for this run, and re-enabling the feature brings it back in place. The
    dropped desktops' names are kept in ``retired_desktop_names`` so restore can remove
    a virtual desktop left over from when the feature was on.
    """
    resolved = resolve_feature_flags(plan, enable=enable, disable=disable)
    if not resolved:
        return plan

    retired_desktop_names = [desktop.name for desktop in plan.desktops if _desktop_switched_off(desktop, resolved)]
    desktops = [
        replace(
            desktop,
            launch=[
                replace(entry, disabled=True)
                if entry.feature is not None and not resolved.get(entry.feature, False) and not entry.disabled
                else entry
                for entry in desktop.launch
            ],
        )
        for desktop in plan.desktops
        if not _desktop_switched_off(desktop, resolved)
    ]
    return WorkspacePlan(
        version=plan.version,
        workspace=plan.workspace,
        personal_workspace=plan.personal_workspace,
        switch_delay_ms=plan.switch_delay_ms,
        desktops=desktops,
        notes=plan.notes,
        aliases=plan.aliases,
        features=resolved,
        retired_desktop_names=retired_desktop_names,
    )


def stored_flag_states() -> list[FlagState]:
    """The rig flags with their `rig.db` overrides; just the defaults when the database cannot be read."""
    try:
        return flag_states()
    except sqlite3.Error as error:
        logging.getLogger(__name__).warning("rig.db unreadable, using flag defaults: %s", error)
        return [FlagState(flag, None) for flag in FLAGS]


def with_stored_flags(plan: WorkspacePlan, states: Sequence[FlagState] | None = None) -> WorkspacePlan:
    """Return ``plan`` with every rig flag declared, layered default < plan `features` < stored override.

    The rig flags registry holds each flag's default, so the machine plan needs no
    `features` block. A plan that does set a flag pins it for that plan (test plans
    rely on this), and an override stored with `rig flags enable|disable` beats both.
    """
    states = stored_flag_states() if states is None else states
    features = {state.flag.name: state.flag.default for state in states}
    features.update(plan.features)
    features.update({state.flag.name: state.override for state in states if state.override is not None})
    return replace(plan, features=features)


def load_plan(
    path: Path,
    *,
    require_existing_paths: bool = False,
    enable_features: Sequence[str] = (),
    disable_features: Sequence[str] = (),
) -> WorkspacePlan:
    plan = with_stored_flags(parse_plan_json(path.read_text(encoding="utf-8"), source=str(path), base_dir=path.parent))
    plan = apply_feature_flags(plan, enable=enable_features, disable=disable_features)
    plan = fit_columns(plan, live_screen_widths())
    return verify_plan(plan, source=str(path), require_existing_paths=require_existing_paths)


def load_plan_context(
    path: Path,
    *,
    enable_features: Sequence[str] = (),
    disable_features: Sequence[str] = (),
) -> WorkspacePlan:
    plan = with_stored_flags(parse_plan_json(path.read_text(encoding="utf-8"), source=str(path), base_dir=path.parent))
    plan = apply_feature_flags(plan, enable=enable_features, disable=disable_features)
    return fit_columns(plan, live_screen_widths())


def save_plan(plan: WorkspacePlan, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(plan.to_dict(), indent=2) + "\n", encoding="utf-8")


def save_plan_bundle(plan: WorkspacePlan, path: Path, *, desktop_dir_name: str = "desktops") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    desktop_dir = path.parent / desktop_dir_name
    desktop_dir.mkdir(parents=True, exist_ok=True)

    desktop_refs: list[JsonValue] = []
    used_names: set[str] = set()
    for index, desktop in enumerate(plan.desktops, start=1):
        base_name = _slugify_filename(desktop.name) or f"desktop-{index}"
        file_name = _dedupe_file_name(base_name, used_names)
        desktop_path = desktop_dir / f"{file_name}.json"
        desktop_path.write_text(json.dumps(desktop.to_dict(), indent=2) + "\n", encoding="utf-8")
        relative_path = Path(os.path.relpath(desktop_path, path.parent)).as_posix()
        desktop_refs.append(desktop.bundle_ref(relative_path))

    path.write_text(json.dumps(plan.bundle_root(desktop_refs), indent=2) + "\n", encoding="utf-8")


def load_machine_plan_template(workspace: Path, *, personal_workspace: Path | None = None) -> WorkspacePlan:
    return load_plan_template(MACHINE_PLAN_TEMPLATE_PATH, workspace, personal_workspace=personal_workspace)


def load_sample_plan_template(workspace: Path) -> WorkspacePlan:
    return load_plan_template(SAMPLE_PLAN_TEMPLATE_PATH, workspace, expected_screen_count=SAMPLE_PLAN_SCREEN_COUNT)


def load_plan_template(
    template_path: Path,
    workspace: Path,
    *,
    personal_workspace: Path | None = None,
    expected_screen_count: int | None = None,
) -> WorkspacePlan:
    plan = parse_plan_json(
        template_path.read_text(encoding="utf-8"), source=str(template_path), base_dir=template_path.parent
    )
    plan.workspace = str(workspace)
    if personal_workspace is not None:
        plan.personal_workspace = str(personal_workspace)
    verify_plan(
        plan, source=str(template_path), expected_screen_count=expected_screen_count, require_existing_paths=False
    )
    return plan


def parse_plan_json(raw_text: str, *, source: str = "<memory>", base_dir: Path | None = None) -> WorkspacePlan:
    data = load_json(raw_text)
    if not is_object(data):
        raise ValueError(PLAN_ENTRY_NOT_OBJECT)
    if "bundle" in data:
        plan = _resolve_plan_bundle(data, source=source, base_dir=base_dir)
    else:
        plan = WorkspacePlan.from_dict(data)
    return fit_columns(plan, {})


def _as_bool(value: object) -> object:
    """A bundle ref's ``disabled`` is read for its truthiness, as Python reads it."""
    return value if value is None else bool(value)


type _Stringified = Annotated[str, BeforeValidator(str)]
type _Truthiness = Annotated[bool | None, BeforeValidator(_as_bool)]


@dataclass(frozen=True, slots=True)
class _BundleBody:
    aliases: dict[str, _Stringified] = field(default_factory=dict[str, str])
    desktops: Lenient[list[object]] = field(default_factory=list[object])


@dataclass(frozen=True, slots=True)
class _PlanBundle:
    """A plan whose desktops live in separate files: ``{"bundle": {...}, "aliases": {...}, ...}``."""

    bundle: _BundleBody
    aliases: Lenient[dict[str, _Stringified]] = None


@dataclass(frozen=True, slots=True)
class _BundleRoot:
    """A bundle plan's own settings: everything but the desktops and aliases, which the bundle supplies."""

    version: int = PLAN_VERSION
    workspace: OptionalText = None
    personal_workspace: Annotated[OptionalText, Field(alias="personal-workspace")] = None
    switch_delay_ms: int = 650
    notes: ListOrEmpty[Text] = field(default_factory=list[str])
    features: DictOrEmpty[FeatureFlag] = field(default_factory=dict[str, bool])


@dataclass(frozen=True, slots=True)
class _RootDesktops:
    """The desktops a bundle plan lists inline, used only when its bundle names none."""

    desktops: ListOrEmpty[DesktopPlan] = field(default_factory=list[DesktopPlan])


@dataclass(frozen=True, slots=True)
class _BundleDesktopRef:
    """One ``bundle.desktops`` entry: the desktop file to load, and what the bundle overrides in it."""

    path: Lenient[StrictStr] = None
    name: OptionalText = None
    feature: OptionalText = None
    disabled: _Truthiness = None


@dataclass(frozen=True, slots=True)
class _DesktopFile:
    """A bundled desktop file. Its name may come from the bundle ref instead."""

    name: OptionalText = None
    launch: ListOrEmpty[LaunchItem] = field(default_factory=list[LaunchEntry])
    captured_windows: ListOrEmpty[WindowSnapshot] = field(default_factory=list[WindowSnapshot])
    disabled: bool = False
    feature: OptionalText = None


@dataclass(frozen=True, slots=True)
class _LoadedDesktop:
    ref: _BundleDesktopRef
    path: Path
    data: object


def _bundled_desktop(loaded: _LoadedDesktop, *, source: str) -> DesktopPlan:
    """The desktop file as the plan sees it, with the bundle ref's overrides applied."""
    desktop = decode_plan_entry(_DesktopFile, loaded.data)
    ref = loaded.ref
    name = ref.name if ref.name is not None else desktop.name
    if name is None:
        raise ValueError(f"Bundle desktop {loaded.path.as_posix()} in {source} must define a name")
    # The ref is where a desktop is switched on or off, so the bundle file alone
    # shows the shape of the workspace without opening every desktop file.
    return DesktopPlan(
        name=name,
        launch=desktop.launch,
        captured_windows=desktop.captured_windows,
        disabled=ref.disabled if ref.disabled is not None else desktop.disabled,
        feature=ref.feature if ref.feature is not None else desktop.feature,
    )


def _resolve_plan_bundle(data: object, *, source: str, base_dir: Path | None) -> WorkspacePlan:
    envelope = try_decode(_PlanBundle, data)
    if envelope is None:
        raise ValueError(PLAN_ENTRY_NOT_OBJECT)
    body = envelope.bundle

    if body.desktops is None:
        raise ValueError(f"Bundle desktops must be a list in {source}")

    loaded: list[_LoadedDesktop] = []
    for item in body.desktops:
        ref = decode_plan_entry(_BundleDesktopRef, item)
        relative_path = ref.path
        if relative_path is None or not relative_path.strip():
            raise ValueError(f"Bundle desktop entries must define a relative path in {source}")
        if base_dir is None:
            raise ValueError(f"Bundle desktop entries in {source} require a filesystem base directory")
        desktop_path = (base_dir / relative_path).resolve()
        desktop_data = load_json(desktop_path.read_text(encoding="utf-8"))
        if not is_object(desktop_data):
            raise ValueError(PLAN_ENTRY_NOT_OBJECT)
        loaded.append(_LoadedDesktop(ref, desktop_path, desktop_data))

    desktops = [_bundled_desktop(desktop, source=source) for desktop in loaded]
    root = decode(_BundleRoot, data)
    if not desktops:
        desktops = decode(_RootDesktops, data).desktops

    aliases = {**(envelope.aliases or {}), **body.aliases}

    return WorkspacePlan(
        version=root.version,
        workspace=root.workspace,
        personal_workspace=root.personal_workspace,
        switch_delay_ms=root.switch_delay_ms,
        desktops=desktops,
        notes=root.notes,
        aliases=aliases,
        features=root.features,
    )


def _slugify_filename(value: str) -> str:
    normalized = re.sub(r"[^A-Za-z0-9]+", "-", value).strip("-").lower()
    return normalized


def _dedupe_file_name(base_name: str, used_names: set[str]) -> str:
    candidate = base_name
    suffix = 2
    while candidate in used_names:
        candidate = f"{base_name}-{suffix}"
        suffix += 1
    used_names.add(candidate)
    return candidate


def verify_plan(
    plan: WorkspacePlan,
    *,
    source: str = "<memory>",
    expected_screen_count: int | None = None,
    require_existing_paths: bool = False,
) -> WorkspacePlan:
    errors = validate_plan(plan, require_existing_paths=require_existing_paths)
    if errors:
        joined = "; ".join(errors)
        raise ValueError(f"Invalid workspace plan {source}: {joined}")
    structure_issues = validate_plan_structure(plan, expected_screen_count=expected_screen_count, strict=False)
    if structure_issues:
        joined = "; ".join(structure_issues)
        raise ValueError(f"Structurally invalid workspace plan {source}: {joined}")
    return plan


def build_sample_plan(workspace: Path, terminal_count: int = 3) -> WorkspacePlan:
    terminals = [
        LaunchEntry(
            name=f"Workspace Terminal {index + 1}",
            type="terminal",
            cwd="{workspace}",
            command="opencode --auto",
            shell="wt-pwsh",  # nosec B604
            window_title=f"Workspace Terminal {index + 1}",
            placement=placement_spec(
                2,
                "thirds",
                min(index + 1, 3),
                subdivide_mode="halves" if index + 1 in {2, 3} else None,
                subdivide_slot=1 if index + 1 == 2 else 2,
                subdivide_orientation="horizontal" if index + 1 in {2, 3} else None,
            )
            if index + 1 in {2, 3}
            else placement_spec(2, "thirds", index + 1),
            window_process_name="WindowsTerminal.exe",
            notes="Desktop 1 terminal that opens the workspace and starts the default coding command.",
        )
        for index in range(max(1, terminal_count))
    ]
    return WorkspacePlan(
        workspace=str(workspace),
        notes=[
            "Desktop 1 terminals launch the default coding command from the configured workspace.",
            "Adjust app paths and arguments for browsers, Slack, Obsidian, or IDE windows before restoring.",
            "Run `uv run desktop-manager jobs install` after you are happy with the plan.",
        ],
        desktops=[
            DesktopPlan(name="Work", launch=terminals),
            DesktopPlan(
                name="Admin",
                launch=[
                    LaunchEntry(
                        name="Obsidian",
                        type="app",
                        path=r"C:\Users\%USERNAME%\AppData\Local\Programs\Obsidian\Obsidian.exe",
                        notes="Replace this path if Obsidian is installed elsewhere.",
                    ),
                    LaunchEntry(
                        name="Browser",
                        type="app",
                        path=r"C:\Program Files\Google\Chrome\Application\chrome.exe",
                        notes="Add URLs or profile flags in `args` if you want a specific browser window.",
                    ),
                ],
            ),
        ],
    )


def build_bespoke_machine_plan(workspace: Path, *, personal_workspace: Path | None = None) -> WorkspacePlan:
    return load_machine_plan_template(workspace, personal_workspace=personal_workspace)


def build_launch_entries_for_desktop(
    windows: list[WindowSnapshot], desktop_number: int, workspace: Path | None
) -> list[LaunchEntry]:
    from desktop_manager.models import WINDOWS_TERMINAL_PROCESSES

    entries: list[LaunchEntry] = []
    terminal_index = 0
    for window in windows:
        process_name = (window.process_name or "").lower()
        if desktop_number == 1 and workspace is not None and process_name in WINDOWS_TERMINAL_PROCESSES:
            terminal_index += 1
            placement = window.inferred_placement or (
                placement_spec(
                    2,
                    "thirds",
                    min(terminal_index, 3),
                    subdivide_mode="halves" if terminal_index in {2, 3} else None,
                    subdivide_slot=1 if terminal_index == 2 else 2,
                    subdivide_orientation="horizontal" if terminal_index in {2, 3} else None,
                )
                if terminal_index in {2, 3}
                else placement_spec(2, "thirds", min(terminal_index, 3))
            )
            entries.append(
                LaunchEntry(
                    name=f"Workspace Terminal {terminal_index}",
                    type="terminal",
                    cwd="{workspace}",
                    command=_inferred_terminal_command(window.title),
                    shell="wt-pwsh",  # nosec B604
                    window_title=f"Workspace Terminal {terminal_index}",
                    placement=placement,
                    window_process_name="WindowsTerminal.exe",
                    notes=f"Generated from captured terminal window: {window.title}",
                )
            )
            continue

        if window.exe_path:
            entries.append(
                LaunchEntry(
                    name=window.title,
                    type="app",
                    path=window.exe_path,
                    placement=window.inferred_placement,
                    window_process_name=window.process_name,
                    notes=(
                        "Captured from the current desktop state. Add args if this app needs a "
                        "project, URL, or profile."
                    ),
                )
            )
    return entries


def _inferred_terminal_command(window_title: str) -> str:
    normalized = window_title.casefold()
    if "copilot" in normalized:
        return "copilot"
    return "opencode"


def substitute_workspace(value: str | None, workspace: str | None) -> str | None:
    if value is None:
        return None
    if workspace:
        value = value.replace("{workspace}", workspace)
    return value


def deferred_logon_terminals(plan: WorkspacePlan) -> list[LaunchEntry]:
    """Enabled terminal entries whose command waits for `claude-sessions startup` at logon, in plan order."""
    return [
        entry
        for desktop in plan.desktops
        if not desktop.disabled
        for entry in desktop.launch
        if not entry.disabled and entry.defers_command_at_logon
    ]


def defer_logon_commands(plan: WorkspacePlan) -> WorkspacePlan:
    """The plan a logon restore runs: entries with `command_at_logon: "deferred"` open a bare shell.

    Only `workspace restore --defer-logon-commands` applies this. A restore without it
    runs every entry's command, so a terminal is never left as a bare shell by a
    restore nothing follows up on.
    """
    return replace(
        plan,
        desktops=[
            replace(
                desktop,
                launch=[
                    replace(entry, command=None) if entry.defers_command_at_logon else entry for entry in desktop.launch
                ],
            )
            for desktop in plan.desktops
        ],
    )
