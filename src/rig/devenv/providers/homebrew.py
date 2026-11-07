"""Homebrew package manager provider for macOS and Linux."""

from __future__ import annotations

import shutil
from typing import override

from rich.console import Console

from .base import PackageManager, run_command

console = Console()


class HomebrewManager(PackageManager):
    """Manages packages via Homebrew (brew)."""

    def __init__(self) -> None:
        self._installed_cache: set[str] | None = None

    @override
    def name(self) -> str:
        return "brew"

    @override
    def is_available(self) -> bool:
        return shutil.which("brew") is not None

    def get_installed_packages(self) -> set[str]:
        """Fetch and cache all installed formulae and casks in a single invocation."""
        if self._installed_cache is None:
            if not self.is_available():
                self._installed_cache = set()
            else:
                res = run_command(["brew", "list"])
                if res.returncode == 0:
                    self._installed_cache = {pkg.strip().lower() for pkg in res.stdout.split()}
                else:
                    self._installed_cache = set()
        return self._installed_cache

    @override
    def is_installed(self, package_id: str) -> bool:
        if not self.is_available():
            return False
        base_name = package_id.rsplit("/", maxsplit=1)[-1].lower()
        return base_name in self.get_installed_packages()

    @override
    def install(self, package_id: str, silent: bool = True) -> bool:
        if not self.is_available():
            return False
        cmd = ["brew", "install", package_id]
        if not silent:
            console.print(f"[cyan]Running: {' '.join(cmd)}[/cyan]")
        res = run_command(cmd, capture_output=silent)
        return res.returncode == 0

    @override
    def update(self, package_id: str, silent: bool = True) -> bool:
        if not self.is_available():
            return False
        cmd = ["brew", "upgrade", package_id]
        if not silent:
            console.print(f"[cyan]Running: {' '.join(cmd)}[/cyan]")
        res = run_command(cmd, capture_output=silent)
        return res.returncode == 0

    @override
    def update_all(self, silent: bool = True) -> bool:
        if not self.is_available():
            return False
        cmd = ["brew", "upgrade"]
        if not silent:
            console.print(f"[cyan]Running: {' '.join(cmd)}[/cyan]")
        res = run_command(cmd, capture_output=silent)
        return res.returncode == 0
