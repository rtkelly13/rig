"""Directory links that work on Windows without the symlink privilege.

Creating a symlink on Windows needs Developer Mode or an elevated token. A
directory junction needs neither, so it is the fallback there. Code that reads
links back has to accept both, because ``Path.is_symlink()`` is ``False`` for a
junction.
"""

from __future__ import annotations

import sys
from pathlib import Path


def is_link(path: Path) -> bool:
    """Whether *path* is a symlink or a Windows junction, dangling or not."""
    return path.is_symlink() or path.is_junction()


def link_or_exists(path: Path) -> bool:
    """Whether anything, including a dangling link, occupies *path*."""
    return is_link(path) or path.exists()


def link_directory(link: Path, target: Path) -> None:
    """Point *link* at the directory *target*: a symlink, else a junction on Windows.

    Raises ``OSError`` when neither can be created.
    """
    try:
        link.symlink_to(target, target_is_directory=True)
    except OSError:
        if sys.platform != "win32":
            raise
        import _winapi

        _winapi.CreateJunction(str(target.resolve()), str(link))
