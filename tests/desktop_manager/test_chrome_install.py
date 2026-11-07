"""The `chrome-tabs` command shim and checkout discovery."""

from __future__ import annotations

from pathlib import Path

import pytest

from desktop_manager.chrome.install import find_checkout, render_shim, write_shim

pytestmark = pytest.mark.ci


def test_shim_forwards_every_argument_to_the_runtime_copy():
    shim = render_shim(Path(r"C:\x y\Scripts\desktop-manager.exe"))
    assert shim == '@echo off\r\n"C:\\x y\\Scripts\\desktop-manager.exe" chrome-tabs %*\r\n'


def test_write_shim_creates_the_bin_directory(tmp_path: Path):
    shim = tmp_path / "bin" / "chrome-tabs.cmd"
    write_shim(tmp_path / "desktop-manager.exe", shim)
    assert shim.read_bytes() == render_shim(tmp_path / "desktop-manager.exe").encode("utf-8")


def test_checkout_is_the_nearest_rig_project(tmp_path: Path):
    (tmp_path / "pyproject.toml").write_text('[project]\nname = "rig"\n', encoding="utf-8")
    nested = tmp_path / "src" / "desktop_manager" / "chrome"
    nested.mkdir(parents=True)
    (tmp_path / "src" / "pyproject.toml").write_text('[project]\nname = "other"\n', encoding="utf-8")
    assert find_checkout(nested) == tmp_path


def test_no_checkout_when_installed(tmp_path: Path):
    assert find_checkout(tmp_path) is None
