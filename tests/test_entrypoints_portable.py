"""Every console script must at least start on Linux.

Modules are imported before ``sys.platform`` is patched: the patch covers what runs
at invocation time. Import-time Windows dependencies are the ruff TID251 rule's job.
"""

from __future__ import annotations

import importlib

import pytest
from typer.testing import CliRunner

from rig.platform_table import (
    END_MARKER,
    REGENERATE_COMMAND,
    REPO_ROOT,
    START_MARKER,
    EntryPoint,
    load_entry_points,
    render_table,
    resolve_app,
    restricted_commands,
    splice_readme,
    walk_commands,
)

ENTRY_POINTS = load_entry_points()
TYPER_ENTRY_POINTS = [entry for entry in ENTRY_POINTS if entry.function == "main"]


def test_every_script_is_covered():
    assert {entry.script for entry in ENTRY_POINTS} >= {"desktop-manager", "help", "md-lint", "workspace"}
    # Every other entry point hands over to the C# CLI; tests/test_csharp_cli.py covers that handover.
    hand_rolled = {
        entry.script for entry in ENTRY_POINTS if entry.function != "main" and entry.target != "rig.csharp_cli:entry"
    }
    assert hand_rolled == set(), "a new non-main entry point needs its own smoke test here"


@pytest.mark.parametrize("entry", ENTRY_POINTS, ids=lambda entry: entry.script)
def test_entry_point_target_imports(entry: EntryPoint):
    module = importlib.import_module(entry.module)
    assert callable(getattr(module, entry.function))


@pytest.mark.parametrize("entry", TYPER_ENTRY_POINTS, ids=lambda entry: entry.script)
def test_help_runs_on_linux(monkeypatch, entry: EntryPoint):
    app = resolve_app(entry)
    assert app is not None
    monkeypatch.setattr("sys.platform", "linux")
    result = CliRunner().invoke(app, ["--help"], prog_name=entry.script)
    assert result.exit_code == 0, result.output
    assert result.exception is None or isinstance(result.exception, SystemExit)
    assert "Traceback" not in result.output


@pytest.mark.parametrize("entry", TYPER_ENTRY_POINTS, ids=lambda entry: entry.script)
def test_every_subcommand_help_runs_on_linux(monkeypatch, entry: EntryPoint):
    app = resolve_app(entry)
    assert app is not None
    records = list(walk_commands(app, entry.script))
    monkeypatch.setattr("sys.platform", "linux")
    runner = CliRunner()
    for record in records:
        result = runner.invoke(app, [*record.path[1:], "--help"], prog_name=entry.script)
        assert result.exit_code == 0, f"{record.invocation} --help: {result.output}"
        assert "Traceback" not in result.output, record.invocation


def test_readme_platform_table_is_current():
    readme = (REPO_ROOT / "README.md").read_text(encoding="utf-8")
    expected = splice_readme(readme, render_table(restricted_commands(ENTRY_POINTS)))
    assert readme == expected, f"README platform table is stale; run `{REGENERATE_COMMAND}`"


def test_splice_readme_replaces_between_markers():
    readme = f"intro\n{START_MARKER}\nold\n{END_MARKER}\noutro\n"
    assert splice_readme(readme, "new") == f"intro\n{START_MARKER}\n\nnew\n\n{END_MARKER}\noutro\n"


def test_splice_readme_needs_markers():
    with pytest.raises(ValueError, match="markers"):
        splice_readme("no markers here", "table")
