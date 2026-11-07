"""Windows Package Manager (winget) provider."""

from __future__ import annotations

import shutil
from typing import override

from rich.console import Console

from .base import PackageManager, run_command

console = Console()


class WingetManager(PackageManager):
    """Manages packages via Windows Package Manager (winget)."""

    @override
    def name(self) -> str:
        return "winget"

    @override
    def is_available(self) -> bool:
        return shutil.which("winget") is not None

    @override
    def is_installed(self, package_id: str) -> bool:
        if not self.is_available():
            return False
        res = run_command(["winget", "list", "--id", package_id, "--exact"])
        return res.returncode == 0 and package_id.lower() in res.stdout.lower()

    @override
    def install(self, package_id: str, silent: bool = True) -> bool:
        if not self.is_available():
            return False
        cmd = [
            "winget",
            "install",
            "--id",
            package_id,
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements",
        ]
        if silent:
            cmd.extend(["--silent", "--disable-interactivity"])
        else:
            console.print(f"[cyan]Running: {' '.join(cmd)}[/cyan]")

        res = run_command(cmd, capture_output=silent)
        return res.returncode == 0

    @override
    def update(self, package_id: str, silent: bool = True) -> bool:
        if not self.is_available():
            return False
        cmd = [
            "winget",
            "upgrade",
            "--id",
            package_id,
            "--exact",
            "--accept-package-agreements",
            "--accept-source-agreements",
        ]
        if silent:
            cmd.extend(["--silent", "--disable-interactivity"])
        else:
            console.print(f"[cyan]Running: {' '.join(cmd)}[/cyan]")

        res = run_command(cmd, capture_output=silent)
        return res.returncode == 0

    @override
    def update_all(self, silent: bool = True) -> bool:
        if not self.is_available():
            return False
        cmd = [
            "winget",
            "upgrade",
            "--all",
            "--accept-package-agreements",
            "--accept-source-agreements",
        ]
        if silent:
            cmd.extend(["--silent", "--disable-interactivity"])
        else:
            console.print(f"[cyan]Running: {' '.join(cmd)}[/cyan]")

        res = run_command(cmd, capture_output=silent)
        return res.returncode == 0
