"""Where the estate is: the code root the estate web routes scan."""

from __future__ import annotations

import pathlib

from rig import roots


def code_root(explicit: str | None) -> pathlib.Path:
    """Return the folder to scan: an explicit path, else the personal folder (`rig.roots`)."""
    if explicit:
        return pathlib.Path(explicit).expanduser().resolve()
    return roots.personal_dir().resolve()
