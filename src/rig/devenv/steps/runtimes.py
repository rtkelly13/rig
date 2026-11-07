"""Runtimes setup step (uv for Python, fnm for Node.js)."""

from __future__ import annotations

import re
import shutil
import subprocess
from pathlib import Path

from rich.console import Console

from ..config.loader import load_all_builtin_tools
from ..engine.context import StepContext
from ..tool_manager import ToolManager

console = Console()


def detect_required_python_version() -> str:
    """Detect required Python version from local workspace or return 3.13 default."""
    cwd = Path.cwd()

    # 1. Check .python-version file
    pv_file = cwd / ".python-version"
    if pv_file.is_file():
        try:
            ver = pv_file.read_text(encoding="utf-8").strip()
            if ver:
                return ver
        except (OSError, UnicodeError):
            pass

    # 2. Check pyproject.toml
    pyproject = cwd / "pyproject.toml"
    if pyproject.is_file():
        try:
            content = pyproject.read_text(encoding="utf-8")
            match = re.search(r'requires-python\s*=\s*["\']([^"\']+)["\']', content)
            if match:
                req = match.group(1).strip()
                # Parse >=3.13 or ~=3.13 or ==3.13
                ver_match = re.search(r"\d+\.\d+", req)
                if ver_match:
                    return ver_match.group(0)
        except (OSError, UnicodeError):
            pass

    return "3.13"


def run_runtimes_setup(ctx: StepContext) -> None:
    """Install and configure core language runtimes."""
    tool_mgr = ToolManager(ctx.platform)
    all_tools = load_all_builtin_tools()

    # 1. Ensure uv is installed
    uv_tool = next((t for t in all_tools if t.id == "uv"), None)
    if uv_tool and not tool_mgr.is_tool_installed(uv_tool):
        console.print("[cyan]Installing uv (Python manager)...[/cyan]")
        if not ctx.dry_run:
            tool_mgr.install_tool(uv_tool, silent=ctx.use_silent_mode)

    if shutil.which("uv"):
        target_py = detect_required_python_version()
        console.print(f"[green]✓ uv is available (target Python: {target_py}).[/green]")
        if not ctx.dry_run:
            console.print(f"[cyan]Ensuring Python {target_py} is available via uv...[/cyan]")
            try:
                subprocess.run(["uv", "python", "install", target_py], check=False, capture_output=True)
            except (OSError, subprocess.SubprocessError) as e:
                console.print(f"[yellow]uv python install encountered: {e}[/yellow]")
    else:
        console.print("[yellow]uv is not found on PATH.[/yellow]")

    # 2. Ensure fnm is installed
    fnm_tool = next((t for t in all_tools if t.id == "fnm"), None)
    if fnm_tool and not tool_mgr.is_tool_installed(fnm_tool):
        console.print("[cyan]Installing fnm (Fast Node Manager)...[/cyan]")
        if not ctx.dry_run:
            tool_mgr.install_tool(fnm_tool, silent=ctx.use_silent_mode)

    if shutil.which("fnm"):
        console.print("[green]✓ fnm is available.[/green]")
        if not ctx.dry_run:
            console.print("[cyan]Ensuring Node LTS is installed via fnm...[/cyan]")
            try:
                subprocess.run(["fnm", "install", "--lts"], check=False, capture_output=True)
            except (OSError, subprocess.SubprocessError) as e:
                console.print(f"[yellow]fnm install --lts encountered: {e}[/yellow]")
    else:
        console.print("[yellow]fnm is not found on PATH.[/yellow]")
