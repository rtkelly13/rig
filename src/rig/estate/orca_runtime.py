"""What Python still needs from Orca's runtime: the unavailable exit code, the estate's profile and `Repo`.

The runtime client itself moved to C# (`src/Rig.Core/Orca/OrcaRuntime.cs`) with `estate orca`.
"""

from __future__ import annotations

import pathlib
import sys
from dataclasses import dataclass
from typing import Annotated

from pydantic import Field

from rig.typed_json import Lenient

#: The exit code a command gives when Orca could not be reached; the job coordinator retries it.
ORCA_UNAVAILABLE_EXIT = 3

#: On Windows the personal estate runs its own Orca profile (`--user-data-dir`) beside the default one.
ESTATE_PROFILE_FOLDER = "orca-personal"


def default_user_data() -> pathlib.Path:
    """Orca's user-data folder on this platform (Electron's `userData` for the app)."""
    if sys.platform == "darwin":
        return pathlib.Path.home() / "Library" / "Application Support" / "orca"
    if sys.platform == "win32":
        return pathlib.Path.home() / "AppData" / "Roaming" / "orca"
    return pathlib.Path.home() / ".config" / "orca"


def estate_user_data() -> pathlib.Path:
    """The Orca profile that holds the personal estate: its own folder on Windows, the default elsewhere."""
    if sys.platform == "win32":
        return default_user_data().with_name(ESTATE_PROFILE_FOLDER)
    return default_user_data()


@dataclass(frozen=True, slots=True)
class Remote:
    canonical_key: Annotated[Lenient[str], Field(alias="canonicalKey")] = None


@dataclass(frozen=True, slots=True)
class Repo:
    """A registered project, as `repo.list` reports it."""

    id: str
    path: str
    display_name: Annotated[str, Field(alias="displayName")] = ""
    group_id: Annotated[Lenient[str], Field(alias="projectGroupId")] = None
    remote: Annotated[Lenient[Remote], Field(alias="gitRemoteIdentity")] = None
