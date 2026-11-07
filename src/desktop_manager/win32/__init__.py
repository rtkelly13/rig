"""Windows desktop automation over pywin32 and pyvda.

- ``desktops``: virtual desktops (count, switch, rename, retire, which desktop a window is on)
- ``geometry``: window rectangles, monitors, moving, raising and closing windows
- ``inventory``: enumerating windows and the processes that own them
- ``entry_windows``: waiting for a launch entry's window and placing it

Importing any of these is safe on every OS; calling one off Windows raises from
:func:`ensure_windows`. Siblings call each other through the module
(``desktops.get_window_desktop_number(...)``), so patching a function on the module
that defines it reaches every caller.
"""

from __future__ import annotations

import sys


def ensure_windows() -> None:
    if sys.platform != "win32":
        raise RuntimeError("Windows virtual desktop automation is only available on Windows.")
