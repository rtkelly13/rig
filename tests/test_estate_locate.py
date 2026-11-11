"""`rig.estate.locate`: the code root, clone discovery provenance and path rendering."""

from __future__ import annotations

import datetime as dt
import io
import pathlib

import pytest
from rich.console import Console

from rig import proc, roots
from rig.estate import discovery, locate


def _console() -> tuple[Console, io.StringIO]:
    buffer = io.StringIO()
    return Console(file=buffer, width=200, color_system=None), buffer


def test_code_root_resolves_an_explicit_path(tmp_path: pathlib.Path) -> None:
    assert locate.code_root(str(tmp_path / "a" / "..")) == tmp_path.resolve()


def test_code_root_defaults_to_the_personal_folder(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(roots, "personal_dir", lambda: tmp_path)
    assert locate.code_root(None) == tmp_path.resolve()


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


def _discover(
    monkeypatch: pytest.MonkeyPatch, paths: list[pathlib.Path], *, from_cache: bool, age: dt.timedelta | None
) -> None:
    def repos(_root: pathlib.Path, *, refresh: bool = False) -> discovery.Discovered:
        del refresh
        return discovery.Discovered(paths=paths, from_cache=from_cache)

    def cache_age(_root: pathlib.Path) -> dt.timedelta | None:
        return age

    monkeypatch.setattr(discovery, "repos", repos)
    monkeypatch.setattr(discovery, "cache_age", cache_age)


def test_clones_reports_a_cached_scan(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    found = [tmp_path / "one", tmp_path / "two"]
    _discover(monkeypatch, found, from_cache=True, age=dt.timedelta(hours=2))
    err, buffer = _console()

    assert locate.clones(tmp_path, refresh=False, quiet=False, err=err) == found
    assert "2 clones" in buffer.getvalue()
    assert "cached 2h ago" in buffer.getvalue()


def test_clones_reports_a_fresh_scan(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _discover(monkeypatch, [tmp_path], from_cache=False, age=None)
    err, buffer = _console()

    locate.clones(tmp_path, refresh=True, quiet=False, err=err)
    assert "freshly scanned" in buffer.getvalue()


def test_clones_stays_silent_when_quiet(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _discover(monkeypatch, [tmp_path], from_cache=True, age=dt.timedelta(minutes=1))
    err, buffer = _console()

    assert locate.clones(tmp_path, refresh=False, quiet=True, err=err) == [tmp_path]
    assert buffer.getvalue() == ""


def test_short_renders_inside_paths_relative_and_outside_paths_in_full(tmp_path: pathlib.Path) -> None:
    assert locate.short(tmp_path / "temp" / "x", tmp_path) == "temp/x"
    outside = tmp_path.parent / "elsewhere"
    assert locate.short(outside, tmp_path) == str(outside)


def test_one_clone_resolves_a_given_path(tmp_path: pathlib.Path) -> None:
    assert locate.one_clone(str(tmp_path)) == tmp_path.resolve()


def test_one_clone_uses_the_enclosing_repo(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    def git_out(*_args: str) -> str:
        return str(tmp_path)

    monkeypatch.setattr(proc, "git_out", git_out)
    assert locate.one_clone(None) == tmp_path


def test_one_clone_falls_back_to_the_working_directory(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    def git_out(*_args: str) -> str:
        return ""

    monkeypatch.setattr(proc, "git_out", git_out)
    monkeypatch.chdir(tmp_path)
    assert locate.one_clone(None) == pathlib.Path.cwd()
