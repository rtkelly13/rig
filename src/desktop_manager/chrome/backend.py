"""Pick this platform's Chrome driver, and hand it to the chrome-tabs commands.

On macOS the driver is `drivers.macos.ChromeMac`; every other platform takes the
Windows path. Each backend is imported lazily, so it is only loaded where it is
used. The `chrome-tabs` app keeps one `ChromeSession` on its Typer context, so a
test (or any caller) passes its own driver with ``obj=ChromeSession(lambda: fake)``.
"""

from __future__ import annotations

import sys
from collections.abc import Callable
from dataclasses import dataclass, field

from desktop_manager.chrome.driver import ChromeDriver


def get_driver() -> ChromeDriver:
    """A new driver for this platform; construct one per command."""
    if sys.platform == "darwin":
        from desktop_manager.chrome.drivers.macos import ChromeMac

        return ChromeMac()
    from desktop_manager.chrome.uia import ChromeUia

    return ChromeUia()


@dataclass(slots=True)
class ChromeSession:
    """One command's driver, made on first use: `traces` and `install` never make one."""

    #: None makes this platform's driver; looked up when used, so the test guard on `get_driver` holds.
    make: Callable[[], ChromeDriver] | None = None
    _driver: ChromeDriver | None = field(default=None, init=False)

    @property
    def driver(self) -> ChromeDriver:
        if self._driver is None:
            self._driver = (self.make or get_driver)()
        return self._driver
