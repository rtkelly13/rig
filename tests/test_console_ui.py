"""Terminal emulation and compatibility test suite for devenv UI.

Verifies that tables, status glyphs, and UI panels degrade safely on legacy,
piped, or non-Unicode terminals without raw box characters or question marks.
"""

from __future__ import annotations

import pytest
from rich import box
from rich.table import Table

from rig import console_ui as ui


def _force_legacy_terminal(monkeypatch: pytest.MonkeyPatch) -> None:
    """Emulate a non-Unicode / legacy terminal environment."""
    monkeypatch.setattr(ui, "_stdout_supports_unicode", lambda *a, **k: False)


def test_table_box_is_ascii_on_legacy_terminal(monkeypatch: pytest.MonkeyPatch) -> None:
    _force_legacy_terminal(monkeypatch)
    assert ui.table_box() is box.ASCII
    assert ui.table_box(box.HEAVY) is box.ASCII


def test_table_box_uses_rounded_when_unicode_supported(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(ui, "_stdout_supports_unicode", lambda *a, **k: True)
    assert ui.table_box() is box.ROUNDED
    assert ui.table_box(box.SIMPLE) is box.SIMPLE


def test_all_status_glyphs_and_box_chars_are_mapped() -> None:
    # Key glyphs used across devenv doctor, steps, and skills output
    status_glyphs = "✓✗↑→●—…•⚠★▶◆◎○"
    heavy_box = "┏┓┗┛┣┫┳┻╋┡┩╇╈━┃─│┼"
    for ch in status_glyphs + heavy_box:
        assert ch in ui._UNICODE_REPLACEMENTS, f"unmapped glyph: {ch!r}"


def test_sanitize_text_cleans_glyphs_on_legacy(monkeypatch: pytest.MonkeyPatch) -> None:
    _force_legacy_terminal(monkeypatch)
    sample = "✓ Installed  ✗ Missing  → Upgrading  ● Agent  — Note … More"
    cleaned = ui.sanitize_text(sample)
    assert cleaned.isascii()
    assert "?" not in cleaned
    assert "OK Installed" in cleaned
    assert "X Missing" in cleaned
    assert "-> Upgrading" in cleaned


def test_doctor_table_emulation_on_legacy_terminal(monkeypatch: pytest.MonkeyPatch) -> None:
    _force_legacy_terminal(monkeypatch)

    table = Table(title="Tools Status", box=ui.table_box(), show_header=True)
    table.add_column("Tool")
    table.add_column("Status")
    table.add_row("Google Antigravity", "✓ Installed")
    table.add_row("Cursor", "✗ Missing")

    output = ui.render_table_to_string(table, force_legacy=True)
    assert output.strip()
    assert output.isascii()
    assert "?" not in output
    assert "Google Antigravity" in output
    assert "OK Installed" in output or "✓" not in output


def test_md_table_generation() -> None:
    headers = ["Tool", "Category", "Status"]
    rows = [
        ["agy", "ai", "✓ Installed"],
        ["uv", "core", "✓ Installed"],
    ]
    md = ui.md_table(headers, rows)
    lines = md.strip().splitlines()
    assert len(lines) == 4
    assert "| Tool | Category | Status |" in lines[0]
    assert "| --- | --- | --- |" in lines[1]
    assert "| agy | ai | ✓ Installed |" in lines[2]


def test_print_json(capsys: pytest.CaptureFixture[str]) -> None:
    data = {"tool": "agy", "status": "active"}
    ui.print_json(data)
    out = capsys.readouterr().out
    assert '"tool": "agy"' in out
    assert '"status": "active"' in out
