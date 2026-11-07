"""Regression guard for the real desktop-manager bundle plan.

Two desktops must not each open the same VS Code ``.code-workspace`` file.
VS Code focuses the already-open workspace window instead of opening a second
one, so a second desktop launching the same ``.code-workspace`` collides onto
the first desktop's window and loses its own slot. Historically both the Review
and Personal desktops opened ``rig.code-workspace``; the Review
desktop now owns it and Personal opens the repo folder instead.
"""

from __future__ import annotations

import re
from collections import defaultdict
from pathlib import Path

from desktop_manager.windows_workspace import load_plan, substitute_placeholders
from rig.flags import FLAGS

BUNDLE_PLAN_PATH = Path(__file__).resolve().parents[2] / "desktop-manager" / "workspace-plan.bundle.json"
_ALL_FEATURES = sorted(flag.name for flag in FLAGS)

_WORKSPACE_FILE_PATTERN = re.compile(r"[^\s\"']+\.code-workspace", re.IGNORECASE)


def _workspace_files_referenced_by_entry(entry, substitutions: dict[str, str]) -> set[str]:
    candidate_strings = [entry.path, entry.command, *entry.args]
    referenced: set[str] = set()
    for candidate in candidate_strings:
        resolved = substitute_placeholders(candidate, substitutions)
        if not resolved:
            continue
        for match in _WORKSPACE_FILE_PATTERN.findall(resolved):
            referenced.add(str(Path(match)).casefold())
    return referenced


def test_no_two_enabled_entries_open_the_same_code_workspace():
    # Every feature on, so a desktop that is paused today (Personal) is still checked.
    plan = load_plan(BUNDLE_PLAN_PATH, enable_features=_ALL_FEATURES)
    substitutions = plan.substitutions()

    owners_by_workspace: dict[str, list[str]] = defaultdict(list)
    for desktop in plan.desktops:
        for entry in desktop.launch:
            if entry.disabled:
                continue
            for workspace_file in _workspace_files_referenced_by_entry(entry, substitutions):
                owners_by_workspace[workspace_file].append(f"{desktop.name}/{entry.name}")

    conflicts = {workspace: owners for workspace, owners in owners_by_workspace.items() if len(owners) > 1}
    assert not conflicts, (
        "Multiple enabled launch entries open the same .code-workspace file, which VS Code "
        f"collapses onto a single window: {conflicts}"
    )


def test_personal_desktop_opens_the_repo_folder_not_the_shared_workspace():
    # Personal sits behind the `personal-desktop` feature; switch it on to inspect it.
    plan = load_plan(BUNDLE_PLAN_PATH, enable_features=["personal-desktop"])
    substitutions = plan.substitutions()

    personal = next(desktop for desktop in plan.desktops if desktop.name == "Personal")
    vscode_entries = [entry for entry in personal.launch if (entry.window_process_name or "").casefold() == "code.exe"]
    assert vscode_entries, "Personal desktop should still launch a VS Code window"
    for entry in vscode_entries:
        assert not _workspace_files_referenced_by_entry(entry, substitutions), (
            f"Personal entry `{entry.name}` should open the repo folder, not a .code-workspace file "
            "(the Review desktop owns rig.code-workspace)"
        )
