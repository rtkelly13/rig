"""`rig.estate.locate`: the code root, path rendering and the clone a command acts on."""

from __future__ import annotations

import datetime as dt
import pathlib

import pytest

from rig import proc, roots
from rig.estate import locate


def test_code_root_resolves_an_explicit_path(tmp_path: pathlib.Path) -> None:
    assert locate.code_root(str(tmp_path / "a" / "..")) == tmp_path.resolve()


def test_code_root_defaults_to_the_personal_folder(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv(roots.CODE_ROOT_ENV, str(tmp_path))
    assert locate.code_root(None) == (tmp_path / "personal").resolve()


@pytest.mark.parametrize(
    ("delta", "expected"),
    [
        (None, "just now"),
        (dt.timedelta(minutes=5), "5m ago"),
        (dt.timedelta(hours=3), "3h ago"),
        (dt.timedelta(days=3), "3d ago"),
    ],
)
def test_ago_renders_each_scale(delta: dt.timedelta | None, expected: str) -> None:
    assert locate.ago(delta) == expected


def test_short_renders_inside_paths_relative_and_outside_paths_in_full(tmp_path: pathlib.Path) -> None:
    assert locate.short(tmp_path / "temp" / "x", tmp_path) == "temp/x"
    outside = tmp_path.parent / "elsewhere"
    assert locate.short(outside, tmp_path) == str(outside)


def test_one_clone_resolves_a_given_path(tmp_path: pathlib.Path) -> None:
    assert locate.one_clone(str(tmp_path)) == tmp_path.resolve()


def test_one_clone_uses_the_repo_enclosing_the_working_directory(
    tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    repo = tmp_path / "repo"
    (repo / "sub").mkdir(parents=True)
    assert proc.git("init", "-q", cwd=repo).ok
    monkeypatch.chdir(repo / "sub")

    assert locate.one_clone(None).resolve() == repo.resolve()
