"""Diagnostic doctor engine for checking environment health."""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

from rich.table import Table

from ...workspace.repo_resolver import check_repo_folder
from ..config.loader import load_all_builtin_tools
from ..engine.context import StepContext, get_current_platform
from ..steps.ai_tools.claude_code import get_claude_settings_path, load_settings
from ..steps.ai_tools.discovery import detect_installed_agents
from ..steps.ai_tools.session_ledger import LedgerHookState, session_ledger_hook_path, session_ledger_state
from ..steps.fonts import NERD_FONTS, is_nerd_font_installed
from ..steps.git import get_git_config_val
from ..steps.github_cli import OverlayState, overlay_state, overlay_target
from ..steps.terminal import get_windows_terminal_status
from ..tool_manager import ToolManager
from ..ui import console, table_box


def run_doctor() -> int:
    """Run full diagnostic suite across all environment components."""
    platform = get_current_platform()
    ctx = StepContext(platform=platform)
    tool_mgr = ToolManager(platform)
    all_tools = load_all_builtin_tools()

    issues_found = 0

    console.print(f"\n[bold cyan]=== Development Environment Doctor ({platform.value}) ===[/bold cyan]\n")

    # 1. Core Tools Check
    t_table = Table(title="1. Tools & Utilities", box=table_box(), show_header=True)
    t_table.add_column("Tool", style="cyan")
    t_table.add_column("Category", style="magenta")
    t_table.add_column("Status", justify="center")

    for tool in all_tools:
        installed = tool_mgr.is_tool_installed(tool)
        status_str = "[green]✓ Installed[/green]" if installed else "[yellow]✗ Missing[/yellow]"
        if not installed and tool.required:
            issues_found += 1
        t_table.add_row(tool.name, tool.category, status_str)

    if ctx.is_windows:
        wt_stat = get_windows_terminal_status()
        if wt_stat.installed:
            if wt_stat.snap_to_grid is False:
                t_table.add_row("Windows Terminal (snapToGrid: off)", "terminal", "[green]✓ Configured[/green]")
            else:
                t_table.add_row("Windows Terminal (snapToGrid: on)", "terminal", "[yellow]! Jitter Risk[/yellow]")
                issues_found += 1

    console.print(t_table)

    console.print()

    # 2. Python Runtime & Quality Toolchain
    p_table = Table(title="2. Python Runtime & Toolchain", box=table_box(), show_header=True)
    p_table.add_column("Component", style="cyan")
    p_table.add_column("Status", justify="center")
    p_table.add_column("Details", style="dim")

    # Python version
    py_ver = f"{sys.version_info.major}.{sys.version_info.minor}.{sys.version_info.micro}"
    py_ok = sys.version_info >= (3, 12)
    p_table.add_row(
        "Python Runtime",
        "[green]✓ Valid[/green]" if py_ok else "[red]✗ Incompatible[/red]",
        f"v{py_ver} ({sys.executable})",
    )
    if not py_ok:
        issues_found += 1

    import importlib.metadata

    # Check uv, ruff, pyright, pytest
    toolchain_binaries = [
        ("uv (Package Manager)", "uv", "uv"),
        ("Ruff (Linter & Formatter)", "ruff", "ruff"),
        ("Pyright (Type Checker)", "pyright", "pyright"),
        ("pytest (Test Suite)", "pytest", "pytest"),
    ]

    for label, pkg, cmd in toolchain_binaries:
        found_path = shutil.which(cmd)
        if found_path:
            try:
                ver_str = f"v{importlib.metadata.version(pkg)}"
                p_table.add_row(label, "[green]✓ Available[/green]", f"{ver_str} ({found_path})")
            except importlib.metadata.PackageNotFoundError:
                p_table.add_row(label, "[green]✓ Available[/green]", found_path)
        else:
            p_table.add_row(label, "[yellow]✗ Missing[/yellow]", f"Install via 'uv add --dev {cmd}'")

    console.print(p_table)
    console.print()

    # 3. Git & SSH Check
    g_table = Table(title="3. Git & SSH Configuration", box=table_box(), show_header=True)
    g_table.add_column("Item", style="cyan")
    g_table.add_column("Status", justify="center")
    g_table.add_column("Details", style="dim")

    git_user = get_git_config_val("user.name")
    git_email = get_git_config_val("user.email")

    if git_user and git_email:
        g_table.add_row("Git Identity", "[green]✓ Configured[/green]", f"{git_user} <{git_email}>")
    else:
        g_table.add_row("Git Identity", "[red]✗ Incomplete[/red]", "Run 'devenv setup' to configure")
        issues_found += 1

    try:
        ssh_key = (Path.home() / ".ssh" / "id_ed25519").exists() or (Path.home() / ".ssh" / "id_rsa").exists()
    except OSError:
        ssh_key = False
    g_table.add_row("SSH Key", "[green]✓ Found[/green]" if ssh_key else "[yellow]! Missing[/yellow]", "~/.ssh")

    repo_check = check_repo_folder(Path.cwd())
    if repo_check.status == "ok":
        g_table.add_row(
            "Repository Folder",
            "[green]✓ Canonical[/green]",
            f"{repo_check.folder_name} (matches upstream {repo_check.owner}/{repo_check.canonical_name})",
        )
    elif repo_check.status == "folder_renamed":
        g_table.add_row(
            "Repository Folder",
            "[yellow]! Renamed Upstream[/yellow]",
            f"Folder '{repo_check.folder_name}' -> canonical '{repo_check.canonical_name}'",
        )
        issues_found += 1
    elif repo_check.status == "folder_mismatch":
        g_table.add_row(
            "Repository Folder",
            "[yellow]! Mismatch[/yellow]",
            f"Folder '{repo_check.folder_name}' != upstream '{repo_check.canonical_name}'",
        )
        issues_found += 1
    elif repo_check.status == "remote_outdated":
        g_table.add_row(
            "Repository Folder",
            "[yellow]! Remote Outdated[/yellow]",
            f"Remote '{repo_check.remote_repo_name}' -> canonical '{repo_check.canonical_name}'",
        )
        issues_found += 1
    elif repo_check.status == "local_only":
        g_table.add_row("Repository Folder", "[dim]- Local/Non-GitHub[/dim]", repo_check.detail)
    else:
        g_table.add_row("Repository Folder", "[dim]- Unresolved[/dim]", repo_check.detail)

    overlay = overlay_state()
    if overlay is OverlayState.IN_SYNC:
        g_table.add_row("gh Scope Overlay", "[green]✓ In sync[/green]", str(overlay_target()))
    else:
        g_table.add_row("gh Scope Overlay", f"[yellow]! {overlay.value.capitalize()}[/yellow]", "Run 'devenv gh'")
        issues_found += 1

    console.print(g_table)
    console.print()

    # 4. Fonts Check
    f_table = Table(title="4. Developer Fonts", box=table_box(), show_header=True)
    f_table.add_column("Font Family", style="cyan")
    f_table.add_column("Status", justify="center")

    for f in NERD_FONTS:
        present = is_nerd_font_installed(ctx, f.name)
        f_table.add_row(f.name, "[green]✓ Installed[/green]" if present else "[dim]- Missing[/dim]")

    console.print(f_table)
    console.print()

    # 5. AI Coding Tools Check
    ai_table = Table(title="5. AI Assistants & Config", box=table_box(), show_header=True)
    ai_table.add_column("Assistant / Config", style="cyan")
    ai_table.add_column("Status", justify="center")

    claude_cfg = get_claude_settings_path().is_file()
    ai_table.add_row("Claude Code Settings", "[green]✓ Configured[/green]" if claude_cfg else "[dim]Not Seeded[/dim]")

    ledger = session_ledger_state(load_settings(get_claude_settings_path()), session_ledger_hook_path())
    if ledger is LedgerHookState.CURRENT:
        ai_table.add_row("Session-Ledger Hook", "[green]✓ Installed[/green]")
    else:
        ai_table.add_row("Session-Ledger Hook", f"[yellow]! {ledger.value.capitalize()} (run 'devenv claude')[/yellow]")
        issues_found += 1

    for agent in detect_installed_agents():
        st = "[green]✓ Available[/green]" if agent.is_installed else "[dim]- Not Found[/dim]"
        ai_table.add_row(f"Agent: {agent.display_name}", st)

    console.print(ai_table)
    console.print()

    if issues_found == 0:
        console.print("[bold green]All required development components are healthy![/bold green]\n")
        return 0

    console.print(
        f"[bold yellow]Found {issues_found} missing required component(s). "
        "Run 'devenv setup' to resolve.[/bold yellow]\n"
    )
    return 1
