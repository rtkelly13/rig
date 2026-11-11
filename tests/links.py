"""Make filesystem links in tests on any OS, without the Windows symlink privilege."""

from __future__ import annotations

import sys
from pathlib import Path

import pytest

_PRIVILEGE_NOT_HELD = 1314


def link_directory(link: Path, target: Path) -> None:
    """Point *link* at the directory *target*: a symlink, else a junction on Windows."""
    try:
        link.symlink_to(target, target_is_directory=True)
    except OSError:
        if sys.platform != "win32":
            raise
        import _winapi  # noqa: TID251 - behind the win32 check

        _winapi.CreateJunction(str(target.resolve()), str(link))


def dir_link(link: Path, target: Path) -> Path:
    """Link *link* to the existing directory *target*: a symlink, or a junction on Windows."""
    link_directory(link, target)
    return link


def dangling_dir_link(link: Path, target: Path) -> Path:
    """Link *link* to a directory that does not exist.

    A junction cannot be created to a missing target, so make the target, link
    it, then remove it.
    """
    target.mkdir(parents=True)
    link_directory(link, target)
    target.rmdir()
    return link


def file_symlink_or_skip(link: Path, target: str) -> Path:
    """Create a file symlink, or skip when Windows withholds the privilege.

    A file cannot be junctioned, so without Developer Mode or elevation there is
    no way to build the fixture.
    """
    try:
        link.symlink_to(target)
    except OSError as err:
        if sys.platform == "win32" and err.winerror == _PRIVILEGE_NOT_HELD:
            pytest.skip("creating a file symlink needs Developer Mode or elevation on Windows")
        raise
    return link
