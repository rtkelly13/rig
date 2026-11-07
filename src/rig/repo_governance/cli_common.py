"""Output shared by the ``repo-governance`` command modules."""

from __future__ import annotations

from rich.console import Console

console = Console()
SEVERITY_STYLES = {"error": "bold red", "warn": "yellow", "info": "dim"}
