"""What Python still needs from Orca's runtime: the unavailable exit code and the estate's profile.

The runtime client itself moved to C# (`src/Rig.Core/Orca/OrcaRuntime.cs`) with `estate orca`.
"""

from __future__ import annotations

import pathlib
import sys

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
