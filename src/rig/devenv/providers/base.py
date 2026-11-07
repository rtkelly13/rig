"""Abstract base class for system package managers."""

from __future__ import annotations

import subprocess
from abc import ABC, abstractmethod


class PackageManager(ABC):
    """Abstract interface for system package managers."""

    @abstractmethod
    def name(self) -> str:
        """Name of the package manager (e.g. brew, winget)."""
        raise NotImplementedError

    @abstractmethod
    def is_available(self) -> bool:
        """Check if package manager binary is present on system PATH."""
        raise NotImplementedError

    @abstractmethod
    def is_installed(self, package_id: str) -> bool:
        """Check if a package is installed."""
        raise NotImplementedError

    @abstractmethod
    def install(self, package_id: str, silent: bool = True) -> bool:
        """Install a package."""
        raise NotImplementedError

    @abstractmethod
    def update(self, package_id: str, silent: bool = True) -> bool:
        """Update a specific package."""
        raise NotImplementedError

    @abstractmethod
    def update_all(self, silent: bool = True) -> bool:
        """Update all installed packages."""
        raise NotImplementedError


def run_command(
    cmd: list[str],
    check: bool = False,
    capture_output: bool = True,
    timeout: int = 300,
) -> subprocess.CompletedProcess[str]:
    """Execute command safely with UTF-8 decoding."""
    return subprocess.run(
        cmd,
        check=check,
        capture_output=capture_output,
        text=True,
        timeout=timeout,
    )
