"""AI coding tools configuration step."""

from __future__ import annotations

from rich.console import Console

from rig import csharp_cli

from ...engine.context import StepContext

console = Console()


def run_ai_tools_setup(ctx: StepContext) -> None:
    """Configure AI agent environments and baselines."""
    # 1. Report installed agents, through `rig devenv agents`
    failed = {"devenv agents": csharp_cli.run(["devenv", "agents"])}
    console.print()

    # 2. Claude Code settings baseline and session-ledger hook, through `rig devenv claude`
    dry_run = ["--dry-run"] if ctx.dry_run else []
    failed["devenv claude"] = csharp_cli.run(["devenv", "claude", *dry_run])

    # 3. OpenCode defaults and policy, through `rig devenv opencode`
    failed["devenv opencode"] = csharp_cli.run(["devenv", "opencode", *dry_run])

    # 4. Configure agent-browser profiles through `rig browser profiles`
    failed["browser profiles"] = csharp_cli.run(["browser", "profiles", *dry_run])

    # 5. Estate-wide agent rules for ~/code/personal, through `rig devenv estate-rules`
    failed["devenv estate-rules"] = csharp_cli.run(["devenv", "estate-rules", *dry_run])

    # 6. Obsidian plugins for the agent-facing vault, through `rig devenv obsidian-plugins` (it skips a missing vault)
    failed["devenv obsidian-plugins"] = csharp_cli.run(["devenv", "obsidian-plugins", *dry_run])

    # 7. Link skills into every agent CLI through `rig skills sync`
    console.print("\n[cyan]Linking skills into agent CLIs...[/cyan]")
    failed["skills sync"] = csharp_cli.run(["skills", "sync", "--system", "all", *dry_run])

    # 8. Agent launcher root shim and shell shortcut, through `rig devenv agent-launcher`
    console.print("\n[cyan]Configuring agent launcher root shim & shell shortcut...[/cyan]")
    failed["devenv agent-launcher"] = csharp_cli.run(["devenv", "agent-launcher", *dry_run])

    # Every part runs even after one fails; the step then reports each failure.
    failures = [f"rig {command} exited {code}" for command, code in failed.items() if code != 0]
    if failures:
        raise RuntimeError("; ".join(failures))
