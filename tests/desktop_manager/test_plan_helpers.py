"""Unit tests for pure helper functions in plan.py and models.py.

These functions have no I/O or OS dependencies and are exercised directly.
"""

from __future__ import annotations

import pytest

from desktop_manager.models import Rect, WindowSnapshot
from desktop_manager.placeholders import substitute_placeholders
from desktop_manager.plan import (
    _dedupe_file_name,
    _inferred_terminal_command,
    _slugify_filename,
    substitute_workspace,
)

# ---------------------------------------------------------------------------
# _slugify_filename
# ---------------------------------------------------------------------------


class TestSlugifyFilename:
    def test_lowercases_and_replaces_spaces(self) -> None:
        assert _slugify_filename("Hello World") == "hello-world"

    def test_replaces_consecutive_special_chars_with_single_dash(self) -> None:
        assert _slugify_filename("foo___bar") == "foo-bar"

    def test_strips_leading_trailing_dashes(self) -> None:
        assert _slugify_filename("---foo---") == "foo"

    def test_preserves_alphanumeric_and_existing_dashes(self) -> None:
        assert _slugify_filename("plan-2024") == "plan-2024"

    def test_replaces_backslash_path_separators(self) -> None:
        result = _slugify_filename(r"C:\path\to file")
        assert result == "c-path-to-file"

    def test_empty_string_returns_empty_string(self) -> None:
        assert _slugify_filename("") == ""

    def test_all_special_chars_returns_empty_string(self) -> None:
        assert _slugify_filename("!@#$%^&*()") == ""


# ---------------------------------------------------------------------------
# _dedupe_file_name
# ---------------------------------------------------------------------------


class TestDedupeFileName:
    def test_returns_name_when_not_in_used_set(self) -> None:
        used: set[str] = set()
        result = _dedupe_file_name("work", used)
        assert result == "work"

    def test_adds_name_to_used_set(self) -> None:
        used: set[str] = set()
        _dedupe_file_name("work", used)
        assert "work" in used

    def test_appends_2_on_first_collision(self) -> None:
        used: set[str] = {"work"}
        result = _dedupe_file_name("work", used)
        assert result == "work-2"

    def test_increments_suffix_on_repeated_collision(self) -> None:
        used: set[str] = {"work", "work-2"}
        result = _dedupe_file_name("work", used)
        assert result == "work-3"

    def test_unique_name_from_populated_set(self) -> None:
        used: set[str] = {"admin", "dev"}
        result = _dedupe_file_name("work", used)
        assert result == "work"
        assert "work" in used

    def test_sequential_deduplication_builds_stable_names(self) -> None:
        used: set[str] = set()
        first = _dedupe_file_name("desktop", used)
        second = _dedupe_file_name("desktop", used)
        third = _dedupe_file_name("desktop", used)
        assert first == "desktop"
        assert second == "desktop-2"
        assert third == "desktop-3"


# ---------------------------------------------------------------------------
# substitute_workspace
# ---------------------------------------------------------------------------


class TestSubstituteWorkspace:
    def test_returns_none_for_none_value(self) -> None:
        assert substitute_workspace(None, r"C:\code\workspace") is None

    def test_replaces_workspace_placeholder(self) -> None:
        result = substitute_workspace("{workspace}", r"C:\code\workspace")
        assert result == r"C:\code\workspace"

    def test_replaces_placeholder_within_path(self) -> None:
        result = substitute_workspace(r"{workspace}\src", r"C:\code\ws")
        assert result == r"C:\code\ws\src"

    def test_returns_value_unchanged_when_no_placeholder(self) -> None:
        result = substitute_workspace(r"C:\absolute\path", r"C:\code\workspace")
        assert result == r"C:\absolute\path"

    def test_returns_value_unchanged_when_workspace_is_none(self) -> None:
        result = substitute_workspace("{workspace}", None)
        assert result == "{workspace}"

    def test_empty_workspace_leaves_placeholder_unchanged(self) -> None:
        # substitute_workspace uses `if workspace:`, so empty string is falsy
        # and the placeholder is not substituted.
        result = substitute_workspace("{workspace}/app", "")
        assert result == "{workspace}/app"


# ---------------------------------------------------------------------------
# _inferred_terminal_command
# ---------------------------------------------------------------------------


class TestInferredTerminalCommand:
    def test_copilot_keyword_returns_copilot(self) -> None:
        assert _inferred_terminal_command("GitHub Copilot - workspace") == "copilot"

    def test_copilot_case_insensitive(self) -> None:
        assert _inferred_terminal_command("COPILOT session") == "copilot"

    def test_generic_title_returns_opencode(self) -> None:
        assert _inferred_terminal_command("PowerShell 7") == "opencode"

    def test_empty_title_returns_opencode(self) -> None:
        assert _inferred_terminal_command("") == "opencode"

    def test_partial_copilot_word_still_matches(self) -> None:
        assert _inferred_terminal_command("my-copilot-session") == "copilot"

    def test_non_copilot_coding_tool_returns_opencode(self) -> None:
        assert _inferred_terminal_command("opencode workspace") == "opencode"


# ---------------------------------------------------------------------------
# substitute_placeholders (happy paths — error paths covered elsewhere)
# ---------------------------------------------------------------------------


class TestSubstitutePlaceholdersHappyPath:
    def test_returns_none_for_none_value(self) -> None:
        assert substitute_placeholders(None, {"workspace": r"C:\code"}) is None

    def test_returns_string_unchanged_when_no_placeholders(self) -> None:
        result = substitute_placeholders(r"C:\absolute\path", {})
        assert result == r"C:\absolute\path"

    def test_substitutes_single_placeholder(self) -> None:
        result = substitute_placeholders("{workspace}", {"workspace": r"C:\code"})
        assert result == r"C:\code"

    def test_substitutes_multiple_different_placeholders(self) -> None:
        result = substitute_placeholders(
            "{root}\\{project}",
            {"root": r"C:\code", "project": "my-app"},
        )
        assert result == r"C:\code\my-app"

    def test_substitutes_same_placeholder_multiple_times(self) -> None:
        result = substitute_placeholders(
            "{ws}/{ws}/nested",
            {"ws": "code"},
        )
        assert result == "code/code/nested"

    def test_empty_substitution_map_leaves_unresolved_and_raises(self) -> None:
        with pytest.raises(ValueError, match="Unresolved placeholders"):
            substitute_placeholders("{workspace}", {})

    def test_empty_string_value_returns_empty_string(self) -> None:
        result = substitute_placeholders("", {"workspace": r"C:\code"})
        assert result == ""


# ---------------------------------------------------------------------------
# Rect dataclass helper tests
# ---------------------------------------------------------------------------


class TestRectProperties:
    def test_right_equals_left_plus_width(self) -> None:
        r = Rect(left=10, top=20, width=100, height=200)
        assert r.right == 110

    def test_bottom_equals_top_plus_height(self) -> None:
        r = Rect(left=10, top=20, width=100, height=200)
        assert r.bottom == 220

    def test_center_x_is_midpoint_of_horizontal_span(self) -> None:
        r = Rect(left=0, top=0, width=200, height=100)
        assert r.center_x == 100

    def test_center_y_is_midpoint_of_vertical_span(self) -> None:
        r = Rect(left=0, top=0, width=200, height=100)
        assert r.center_y == 50

    def test_center_x_with_non_zero_left(self) -> None:
        r = Rect(left=100, top=0, width=200, height=100)
        assert r.center_x == 200

    def test_center_y_with_non_zero_top(self) -> None:
        r = Rect(left=0, top=50, width=200, height=100)
        assert r.center_y == 100

    def test_center_x_integer_division_floors(self) -> None:
        r = Rect(left=0, top=0, width=101, height=100)
        assert r.center_x == 50  # 101 // 2 == 50


class TestRectSerialisation:
    def test_to_dict_returns_all_fields(self) -> None:
        r = Rect(left=1, top=2, width=3, height=4)
        assert r.to_dict() == {"left": 1, "top": 2, "width": 3, "height": 4}

    def test_from_dict_roundtrip(self) -> None:
        original = Rect(left=10, top=20, width=1920, height=1080)
        restored = Rect.from_dict(original.to_dict())
        assert restored == original

    def test_from_dict_coerces_string_values(self) -> None:
        r = Rect.from_dict({"left": "0", "top": "0", "width": "1920", "height": "1080"})
        assert r == Rect(left=0, top=0, width=1920, height=1080)


# ---------------------------------------------------------------------------
# Optional rect helper tests
# ---------------------------------------------------------------------------


def _optional_rect(value: object) -> Rect | None:
    return WindowSnapshot.from_dict({"title": "t", "bounds": value}).bounds


class TestOptionalRect:
    def test_returns_none_for_none_input(self) -> None:
        assert _optional_rect(None) is None

    def test_returns_none_for_string_input(self) -> None:
        assert _optional_rect("1920x1080") is None

    def test_returns_rect_from_valid_dict(self) -> None:
        result = _optional_rect({"left": 0, "top": 0, "width": 1920, "height": 1080})
        assert result == Rect(left=0, top=0, width=1920, height=1080)

    def test_returns_none_from_incomplete_dict(self) -> None:
        assert _optional_rect({"left": 0, "top": 0}) is None

    def test_returns_rect_from_4_element_int_list(self) -> None:
        result = _optional_rect([0, 0, 1920, 1080])
        assert result == Rect(left=0, top=0, width=1920, height=1080)

    def test_returns_none_from_3_element_list(self) -> None:
        assert _optional_rect([0, 0, 1920]) is None

    def test_returns_none_from_list_with_none_element(self) -> None:

        assert _optional_rect([0, 0, None, 1080]) is None

    def test_returns_none_from_empty_list(self) -> None:
        assert _optional_rect([]) is None
