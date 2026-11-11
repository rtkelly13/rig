"""`rig.estate.locate`: the code root."""

from __future__ import annotations

import pathlib

import pytest

from rig import roots
from rig.estate import locate


def test_code_root_resolves_an_explicit_path(tmp_path: pathlib.Path) -> None:
    assert locate.code_root(str(tmp_path / "a" / "..")) == tmp_path.resolve()


def test_code_root_defaults_to_the_personal_folder(tmp_path: pathlib.Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv(roots.CODE_ROOT_ENV, str(tmp_path))
    assert locate.code_root(None) == (tmp_path / "personal").resolve()
