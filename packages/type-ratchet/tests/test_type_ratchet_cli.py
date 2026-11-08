from __future__ import annotations

import subprocess
from pathlib import Path

import pytest
from type_ratchet import RatchetConfig, Rule, measure
from type_ratchet.cli import EXIT_USAGE, EXIT_VIOLATIONS, run

pytestmark = pytest.mark.ci


def _repo(tmp_path: Path, files: dict[str, str]) -> Path:
    for rel, text in files.items():
        path = tmp_path / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        _ = path.write_text(text, encoding="utf-8")
    _ = subprocess.run(["git", "init", "-q"], cwd=tmp_path, check=True)
    _ = subprocess.run(["git", "add", "-A"], cwd=tmp_path, check=True)
    return tmp_path


def test_measure_checks_only_tracked_files_under_the_configured_paths(tmp_path: Path) -> None:
    root = _repo(
        tmp_path,
        {"lib/a.py": "from typing import cast\nx = cast(int, 1)\n", "src/b.py": "x = getattr(1, 'y')\n"},
    )
    _ = (root / "lib" / "untracked.py").write_text("from typing import cast\ncast(int, 1)\n", encoding="utf-8")
    actual = measure(root, RatchetConfig(paths=("lib",)))
    assert actual[Rule.CAST] == {"lib/a.py": 1}
    assert actual[Rule.GETATTR_FAMILY] == {}


def test_measure_with_root_path_checks_the_whole_repo(tmp_path: Path) -> None:
    root = _repo(tmp_path, {"a.py": "x = getattr(1, 'y')\n", "pkg/b.py": "x = getattr(1, 'y')\n"})
    assert measure(root, RatchetConfig(paths=(".",)))[Rule.GETATTR_FAMILY] == {"a.py": 1, "pkg/b.py": 1}


def test_cli_fails_on_violations_and_prints_the_hint(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    root = _repo(
        tmp_path,
        {
            "pyproject.toml": '[tool.type-ratchet]\nhint = "Read the guide."\n',
            "src/a.py": "x = 1  # noqa: E501\n",
        },
    )
    assert run(["--root", str(root), "--report"]) == EXIT_VIOLATIONS
    out = capsys.readouterr().out
    assert "src/a.py: 1 noqa" in out
    assert "Read the guide." in out
    assert "Type-soup gate" in out


def test_cli_passes_a_clean_repo(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    root = _repo(tmp_path, {"src/a.py": "x = 1\n"})
    assert run(["--root", str(root)]) == 0
    assert "No gated type soup." in capsys.readouterr().out


def test_cli_reports_bad_config(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    root = _repo(tmp_path, {"pyproject.toml": '[tool.type-ratchet]\ndisable = ["nope"]\n'})
    assert run(["--root", str(root)]) == EXIT_USAGE
    assert "unknown rules: nope" in capsys.readouterr().err


def test_measure_skips_a_tracked_file_deleted_from_disk(tmp_path: Path) -> None:
    root = _repo(tmp_path, {"src/gone.py": "x = getattr(1, 'y')\n", "src/kept.py": "x = 1\n"})
    (root / "src" / "gone.py").unlink()
    assert measure(root)[Rule.GETATTR_FAMILY] == {}


def test_cli_reports_a_root_outside_git_as_a_usage_error(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, capsys: pytest.CaptureFixture[str]
) -> None:
    monkeypatch.setenv("GIT_CEILING_DIRECTORIES", str(tmp_path.parent))
    _ = (tmp_path / "a.py").write_text("x = 1\n", encoding="utf-8")
    assert run(["--root", str(tmp_path)]) == EXIT_USAGE
    assert "not inside a git work tree" in capsys.readouterr().err


def test_a_rule_set_gates_its_own_rules_in_its_files(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    smells = "from typing import cast\nx = cast(int, getattr(1, 'y'))\n"
    root = _repo(
        tmp_path,
        {
            "pyproject.toml": (
                '[tool.type-ratchet]\n[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/*"]\nrules = ["cast"]\n'
            ),
            "src/a.py": smells,
            "tests/test_a.py": smells,
            "docs/b.py": smells,
        },
    )
    assert run(["--root", str(root)]) == EXIT_VIOLATIONS
    out = capsys.readouterr().out.splitlines()
    assert out[:3] == ["src/a.py: 1 cast", "tests/test_a.py: 1 cast", "src/a.py: 1 getattr-family"]
    assert not any(line.startswith("docs/") for line in out)
    assert "tests/test_a.py: 1 getattr-family" not in out


def test_fakes_live_in_their_home_and_patching_only_in_allowed_files(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    patched = "def test(monkeypatch):\n    monkeypatch.setattr(m, 'f', 1)\n"
    root = _repo(
        tmp_path,
        {
            "pyproject.toml": (
                '[tool.type-ratchet]\npatching-allow = ["tests/old_*"]\nfake-location-allow = ["tests/old_*"]\n'
                '[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/fakes/*"]\nrules = ["patching"]\n'
                '[[tool.type-ratchet.rule-sets]]\nfiles = ["tests/*"]\nrules = ["fake-location", "patching"]\n'
            ),
            "src/a.py": "x = 1\n",
            "tests/fakes/gh.py": "class FakeGh: ...\n",
            "tests/old_test.py": "class FakeOld: ...\n" + patched,
            "tests/test_new.py": "class FakeNew: ...\n" + patched,
        },
    )
    assert run(["--root", str(root)]) == EXIT_VIOLATIONS
    out = capsys.readouterr().out.splitlines()
    assert out[:2] == ["tests/test_new.py: 1 fake-location", "tests/test_new.py: 1 patching"]
