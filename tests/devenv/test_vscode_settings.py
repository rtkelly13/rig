"""VS Code settings rules: plan, comment-preserving merge, and backups."""

from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path

import pytest

from rig import jsonc
from rig.devenv.engine.context import Platform, StepContext
from rig.devenv.steps import vscode
from rig.devenv.steps.vscode import Change, ChangeKind, Rules

pytestmark = pytest.mark.ci

RULES: dict[str, object] = {
    "files.exclude": {"**/.worktrees": True, "**/temp": True},
    "files.watcherExclude": {"**/.worktrees/**": True, "**/temp/**": True},
}
COMMENTS = {
    "files.exclude": ("rig: hide worktrees.",),
    "files.watcherExclude": ("rig: don't watch them.", "Second line."),
}
COMMENTED = Rules(RULES, COMMENTS)

# Shaped like a real settings.json: comments, commented-out keys, trailing commas.
USER_SETTINGS = """{
    // Background auto-update is blocked; keep manual.
    "update.mode": "manual",
    //"workbench.sideBar.location": "right",
    "files.exclude": {
        "**/.DS_Store": true,
    },
    "editor.fontSize": 16,
    // "gitlens.keymap": "alternate",
}
"""


@pytest.fixture
def user_dir(tmp_path: Path) -> Path:
    folder = tmp_path / "Code" / "User"
    (folder / "snippets").mkdir(parents=True)
    (folder / "snippets" / "python.json").write_text("{}", encoding="utf-8")
    (folder / "settings.json").write_text(USER_SETTINGS, encoding="utf-8")
    (folder / "keybindings.json").write_text("[]", encoding="utf-8")
    return folder


def test_plan_adds_missing_keys_and_entries() -> None:
    observed: dict[str, object] = {"files.exclude": {"**/.DS_Store": True}}

    assert vscode.plan(RULES, observed) == [
        Change(ChangeKind.ADD, ("files.exclude", "**/.worktrees"), True),
        Change(ChangeKind.ADD, ("files.exclude", "**/temp"), True),
        Change(ChangeKind.ADD, ("files.watcherExclude",), RULES["files.watcherExclude"]),
    ]


def test_plan_reports_a_value_the_user_set_differently_as_a_conflict() -> None:
    observed: dict[str, object] = {
        "files.exclude": {"**/.worktrees": False, "**/temp": True},
        "files.watcherExclude": "not an object",
    }

    assert vscode.plan(RULES, observed) == [
        Change(ChangeKind.CONFLICT, ("files.exclude", "**/.worktrees"), True, False),
        Change(ChangeKind.CONFLICT, ("files.watcherExclude",), RULES["files.watcherExclude"], "not an object"),
    ]


def test_plan_is_empty_when_rules_are_met() -> None:
    observed: dict[str, object] = {**RULES, "editor.fontSize": 16}
    assert vscode.plan(RULES, observed) == []


def test_apply_keeps_every_original_line_and_satisfies_the_rules() -> None:
    changes = vscode.plan(RULES, vscode.parse_settings(USER_SETTINGS))

    merged = vscode.apply_text(USER_SETTINGS, changes)

    original_lines = [line for line in USER_SETTINGS.splitlines() if line.strip()]
    assert all(line in merged.splitlines() for line in original_lines)
    assert vscode.plan(RULES, vscode.parse_settings(merged)) == []
    assert '        "**/.worktrees": true,' in merged.splitlines()


def test_apply_to_an_empty_object_leaves_valid_json() -> None:
    merged = vscode.apply_text("{}\n", vscode.plan(RULES, {}))

    assert jsonc.loads(merged) == RULES
    assert ",\n}" not in merged


def test_sync_backs_up_then_writes_and_a_second_run_changes_nothing(user_dir: Path, tmp_path: Path) -> None:
    backups = tmp_path / "backups"
    ctx = StepContext(platform=Platform.WINDOWS)

    first = vscode.sync_settings(
        ctx, user_dir=user_dir, backup_root=backups, desired=Rules(RULES), extensions="a.b@1.0\n"
    )

    assert {c.kind for c in first} == {ChangeKind.ADD}
    (snapshot,) = list(backups.iterdir())
    assert (snapshot / "settings.json").read_text(encoding="utf-8") == USER_SETTINGS
    assert (snapshot / "keybindings.json").is_file()
    assert (snapshot / "snippets" / "python.json").is_file()
    assert (snapshot / "extensions.txt").read_text(encoding="utf-8") == "a.b@1.0\n"
    written = (user_dir / "settings.json").read_text(encoding="utf-8")
    assert "// Background auto-update is blocked; keep manual." in written

    second = vscode.sync_settings(ctx, user_dir=user_dir, backup_root=backups, desired=Rules(RULES))

    assert second == []
    assert len(list(backups.iterdir())) == 1


def test_dry_run_writes_nothing(user_dir: Path, tmp_path: Path) -> None:
    ctx = StepContext(platform=Platform.WINDOWS, dry_run=True)

    changes = vscode.sync_settings(ctx, user_dir=user_dir, backup_root=tmp_path / "backups", desired=Rules(RULES))

    assert changes
    assert (user_dir / "settings.json").read_text(encoding="utf-8") == USER_SETTINGS
    assert not (tmp_path / "backups").exists()


def test_a_conflict_is_left_as_the_user_set_it(user_dir: Path, tmp_path: Path) -> None:
    text = (
        "{\n"
        '    "files.exclude": {"**/.worktrees": false, "**/temp": true},\n'
        '    "files.watcherExclude": {"**/.worktrees/**": true, "**/temp/**": true}\n'
        "}\n"
    )
    (user_dir / "settings.json").write_text(text, encoding="utf-8")

    changes = vscode.sync_settings(
        StepContext(platform=Platform.WINDOWS), user_dir=user_dir, backup_root=tmp_path / "b", desired=Rules(RULES)
    )

    assert [c.kind for c in changes] == [ChangeKind.CONFLICT]
    assert (user_dir / "settings.json").read_text(encoding="utf-8") == text


def test_backup_keeps_only_the_newest(user_dir: Path, tmp_path: Path) -> None:
    root = tmp_path / "backups"
    for day in range(1, 5):
        vscode.backup(user_dir, root, extensions=None, now=datetime(2026, 11, day, tzinfo=UTC), keep=2)

    assert sorted(p.name[:8] for p in root.iterdir()) == ["20261103", "20261104"]


def test_user_settings_dir_per_platform(tmp_path: Path) -> None:
    home = tmp_path / "home"
    windows = vscode.user_settings_dir(
        Platform.WINDOWS, env=vscode.ConfigEnv(appdata=str(tmp_path / "Roaming")), home=home
    )
    macos = vscode.user_settings_dir(Platform.MACOS, env=vscode.ConfigEnv(), home=home)
    linux = vscode.user_settings_dir(Platform.LINUX, env=vscode.ConfigEnv(), home=home)

    assert windows == tmp_path / "Roaming" / "Code" / "User"
    assert macos == home / "Library" / "Application Support" / "Code" / "User"
    assert linux == home / ".config" / "Code" / "User"


def test_bundled_rules_ignore_worktrees_and_temp_and_turn_off_vscode_ai() -> None:
    rules = vscode.load_desired()

    assert rules.values == {**RULES, "chat.disableAIFeatures": True}
    assert set(rules.comments) == set(rules.values)
    assert any("set this to false" in line for line in rules.comments["chat.disableAIFeatures"])


def test_an_added_key_is_written_under_its_comment() -> None:
    merged = vscode.apply_text("{}\n", vscode.plan(RULES, {}, comments=COMMENTS))

    assert '    // rig: don\'t watch them.\n    // Second line.\n    "files.watcherExclude"' in merged
    assert vscode.comments_by_key(merged) == COMMENTS


def test_a_missing_comment_goes_above_the_key_and_keeps_the_users_own() -> None:
    text = (
        "{\n"
        "    // my own note\n"
        '    "files.exclude": {"**/.worktrees": true, "**/temp": true},\n'
        '    "files.watcherExclude": {"**/.worktrees/**": true, "**/temp/**": true},\n'
        "}\n"
    )
    changes = vscode.plan(
        RULES, vscode.parse_settings(text), comments=COMMENTS, observed_comments=vscode.comments_by_key(text)
    )

    assert [(c.kind, c.path) for c in changes] == [
        (ChangeKind.COMMENT, ("files.exclude",)),
        (ChangeKind.COMMENT, ("files.watcherExclude",)),
    ]
    merged = vscode.apply_text(text, changes)
    assert vscode.comments_by_key(merged) == {
        "files.exclude": ("my own note", "rig: hide worktrees."),
        "files.watcherExclude": COMMENTS["files.watcherExclude"],
    }
    assert (
        vscode.plan(
            RULES, vscode.parse_settings(merged), comments=COMMENTS, observed_comments=vscode.comments_by_key(merged)
        )
        == []
    )


def test_a_conflicting_key_gets_no_comment() -> None:
    observed: dict[str, object] = {**RULES, "files.watcherExclude": "off"}

    changes = vscode.plan(
        RULES, observed, comments=COMMENTS, observed_comments={"files.exclude": COMMENTS["files.exclude"]}
    )

    assert [c.kind for c in changes] == [ChangeKind.CONFLICT]


def test_sync_adds_comments_once(user_dir: Path, tmp_path: Path) -> None:
    ctx = StepContext(platform=Platform.WINDOWS)
    backups = tmp_path / "backups"

    vscode.sync_settings(ctx, user_dir=user_dir, backup_root=backups, desired=COMMENTED)
    second = vscode.sync_settings(ctx, user_dir=user_dir, backup_root=backups, desired=COMMENTED)

    assert second == []
    written = (user_dir / "settings.json").read_text(encoding="utf-8")
    assert written.count("// rig: hide worktrees.") == 1
    assert "// Background auto-update is blocked; keep manual." in written
