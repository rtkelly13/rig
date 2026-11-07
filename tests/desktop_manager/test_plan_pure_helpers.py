"""Unit tests for pure helper functions in desktop_manager/plan.py.

These tests exercise path helpers, string utilities, and log-path builders —
no OS calls, no filesystem I/O (except via tmp_path), no Windows API.
"""

from __future__ import annotations

from datetime import datetime
from pathlib import Path

import pytest

from desktop_manager.models import DesktopPlan, WorkspacePlan
from desktop_manager.plan import (
    _dedupe_file_name,
    _slugify_filename,
    restore_log_paths,
    substitute_workspace,
    workspace_logs_dir,
)
from desktop_manager.restore_planner import resolve_target_desktop_index

# ---------------------------------------------------------------------------
# _slugify_filename
# ---------------------------------------------------------------------------


class TestSlugifyFilename:
    def test_lowercase_simple_word(self) -> None:
        assert _slugify_filename("Work") == "work"

    def test_spaces_become_hyphens(self) -> None:
        assert _slugify_filename("My Desktop") == "my-desktop"

    def test_multiple_spaces_become_single_hyphen(self) -> None:
        assert _slugify_filename("A  B") == "a-b"

    def test_special_chars_removed(self) -> None:
        assert _slugify_filename("Dev & Admin!") == "dev-admin"

    def test_leading_and_trailing_hyphens_stripped(self) -> None:
        assert _slugify_filename("!Work!") == "work"

    def test_empty_string_returns_empty(self) -> None:
        assert _slugify_filename("") == ""

    def test_all_special_chars_returns_empty(self) -> None:
        assert _slugify_filename("!!!") == ""

    def test_numbers_preserved(self) -> None:
        assert _slugify_filename("Desktop 1") == "desktop-1"

    def test_mixed_case_lowercased(self) -> None:
        assert _slugify_filename("MyWorkspace") == "myworkspace"

    def test_hyphens_in_input_preserved(self) -> None:
        assert _slugify_filename("my-desktop") == "my-desktop"


# ---------------------------------------------------------------------------
# _dedupe_file_name
# ---------------------------------------------------------------------------


class TestDedupeFileName:
    def test_new_name_returned_unchanged(self) -> None:
        used: set[str] = set()
        result = _dedupe_file_name("work", used)
        assert result == "work"

    def test_new_name_added_to_used(self) -> None:
        used: set[str] = set()
        _dedupe_file_name("work", used)
        assert "work" in used

    def test_duplicate_gets_suffix_2(self) -> None:
        used: set[str] = {"work"}
        result = _dedupe_file_name("work", used)
        assert result == "work-2"

    def test_second_duplicate_gets_suffix_3(self) -> None:
        used: set[str] = {"work", "work-2"}
        result = _dedupe_file_name("work", used)
        assert result == "work-3"

    def test_sequential_deduplication(self) -> None:
        used: set[str] = set()
        r1 = _dedupe_file_name("admin", used)
        r2 = _dedupe_file_name("admin", used)
        r3 = _dedupe_file_name("admin", used)
        assert r1 == "admin"
        assert r2 == "admin-2"
        assert r3 == "admin-3"

    def test_different_bases_do_not_collide(self) -> None:
        used: set[str] = set()
        r1 = _dedupe_file_name("work", used)
        r2 = _dedupe_file_name("admin", used)
        assert r1 == "work"
        assert r2 == "admin"


# ---------------------------------------------------------------------------
# substitute_workspace
# ---------------------------------------------------------------------------


class TestSubstituteWorkspace:
    def test_none_value_returns_none(self) -> None:
        assert substitute_workspace(None, r"C:\code") is None

    def test_no_placeholder_returns_unchanged(self) -> None:
        assert substitute_workspace(r"C:\other\path", r"C:\code") == r"C:\other\path"

    def test_placeholder_replaced_with_workspace(self) -> None:
        result = substitute_workspace("{workspace}/src", r"C:\code\myproject")
        assert result == r"C:\code\myproject/src"

    def test_none_workspace_leaves_placeholder(self) -> None:
        result = substitute_workspace("{workspace}/src", None)
        assert result == "{workspace}/src"

    def test_multiple_occurrences_all_replaced(self) -> None:
        result = substitute_workspace("{workspace}/{workspace}", r"C:\ws")
        assert result == r"C:\ws/C:\ws"

    def test_empty_workspace_does_not_replace(self) -> None:
        result = substitute_workspace("{workspace}/src", "")
        assert result == "{workspace}/src"


# ---------------------------------------------------------------------------
# workspace_logs_dir
# ---------------------------------------------------------------------------


class TestWorkspaceLogsDir:
    def test_returns_logs_subdir_of_config_parent(self, tmp_path: Path) -> None:
        config_path = tmp_path / "config" / "windows-workspace.json"
        result = workspace_logs_dir(config_path)
        assert result == tmp_path / "config" / "logs"

    def test_result_is_path_object(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        result = workspace_logs_dir(config_path)
        assert isinstance(result, Path)


# ---------------------------------------------------------------------------
# restore_log_paths
# ---------------------------------------------------------------------------


class TestRestoreLogPaths:
    def test_run_log_uses_timestamp(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        now = datetime(2025, 6, 15, 10, 30, 45)
        run_log = restore_log_paths(config_path, now=now).run
        assert "20250615-103045" in run_log.name

    def test_run_log_in_logs_dir(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        now = datetime(2025, 1, 1, 0, 0, 0)
        run_log = restore_log_paths(config_path, now=now).run
        assert run_log.parent == workspace_logs_dir(config_path)

    def test_latest_log_fixed_name(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        latest_log = restore_log_paths(config_path, now=datetime(2025, 1, 1)).latest
        assert latest_log.name == "workspace-restore-latest.log"

    def test_latest_log_in_same_dir_as_run_log(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        paths = restore_log_paths(config_path, now=datetime(2025, 1, 1))
        assert paths.run.parent == paths.latest.parent

    def test_different_timestamps_produce_different_run_logs(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        run_log_a = restore_log_paths(config_path, now=datetime(2025, 1, 1, 9, 0, 0)).run
        run_log_b = restore_log_paths(config_path, now=datetime(2025, 1, 1, 9, 0, 1)).run
        assert run_log_a != run_log_b

    def test_run_log_has_log_extension(self, tmp_path: Path) -> None:
        config_path = tmp_path / "plan.json"
        run_log = restore_log_paths(config_path, now=datetime(2025, 3, 22, 8, 15, 5)).run
        assert run_log.suffix == ".log"


# ---------------------------------------------------------------------------
# resolve_target_desktop_index (additional edge cases)
# ---------------------------------------------------------------------------


class TestResolveTargetDesktopIndex:
    def _plan(self, *names: str) -> WorkspacePlan:
        return WorkspacePlan(desktops=[DesktopPlan(name=n) for n in names])

    def test_returns_none_when_target_is_none(self) -> None:
        plan = self._plan("Work", "Admin")
        assert resolve_target_desktop_index(plan, None) is None

    def test_match_is_case_insensitive(self) -> None:
        plan = self._plan("Work", "Admin")
        assert resolve_target_desktop_index(plan, "admin") == 2

    def test_match_case_insensitive_uppercase(self) -> None:
        plan = self._plan("Work", "Admin")
        assert resolve_target_desktop_index(plan, "WORK") == 1

    def test_returns_first_desktop_index_one(self) -> None:
        plan = self._plan("Alpha", "Beta", "Gamma")
        assert resolve_target_desktop_index(plan, "Alpha") == 1

    def test_returns_third_desktop_index_three(self) -> None:
        plan = self._plan("Alpha", "Beta", "Gamma")
        assert resolve_target_desktop_index(plan, "Gamma") == 3

    def test_raises_for_unknown_target(self) -> None:
        plan = self._plan("Work", "Admin")
        with pytest.raises(ValueError, match="Desktop 'Nonexistent' not found"):
            resolve_target_desktop_index(plan, "Nonexistent")

    def test_error_message_lists_available_desktops(self) -> None:
        plan = self._plan("Work", "Admin")
        with pytest.raises(ValueError, match="Work") as exc_info:
            resolve_target_desktop_index(plan, "Missing")
        assert "Admin" in str(exc_info.value)
