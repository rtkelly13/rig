"""`vault knowledge-server`: the one shared OpenKnowledge server (see `rig.obsidian.knowledge_server`)."""

from __future__ import annotations

import shutil
import sys

import typer
from rich.console import Console

from rig import proc
from rig.obsidian import knowledge_server as ks

server_app = typer.Typer(
    help="One OpenKnowledge server for the vault, shared by every Claude session (macOS LaunchAgent).",
    no_args_is_help=True,
)
console = Console()

#: `status` exits with this when the server is down or Claude does not use it.
UNHEALTHY_EXIT = 1


def _require_darwin() -> None:
    if sys.platform != "darwin":
        console.print("[yellow]the shared server runs as a macOS LaunchAgent; nothing to do here[/yellow]")
        raise typer.Exit(code=2)


@server_app.command("install")
def install_command() -> None:
    """Install the pinned package under rig's data folder and load the LaunchAgent. Idempotent."""
    _require_darwin()
    script = ks.install()
    path = ks.install_service(script)
    console.print(f"[green]open-knowledge {ks.VERSION}[/green] LaunchAgent loaded: {path}")
    console.print(f"editor {ks.editor_url()}  connector {ks.mcp_url()}  log {ks.log_path()}")
    if not ks.current_registration().shared:
        console.print("Claude still starts its own copy per session: run `vault knowledge-server connect`.")


@server_app.command("restart")
def restart_command() -> None:
    """Restart the server, for example after the vault's `.ok/config.yml` changes."""
    _require_darwin()
    from rig import launchd

    launchd.kickstart(ks.LABEL, restart=True)
    console.print("restarted")


@server_app.command("uninstall")
def uninstall_command() -> None:
    """Unload and remove the LaunchAgent. Claude's entry then points at nothing: `ok init` in the vault
    registers the per-session one again."""
    _require_darwin()
    console.print("removed" if ks.uninstall_service() else "no LaunchAgent installed")


@server_app.command("connect")
def connect_command(
    apply: bool = typer.Option(False, "--apply", help="Make the change; without it, print what would run"),
) -> None:
    """Point Claude Code's user-scope `open-knowledge` entry at the shared server.

    Sessions already running keep the connector they started with until they restart.
    """
    have = ks.current_registration()
    if have.shared:
        console.print(f"[green]already shared[/green]: {ks.mcp_url()}")
        return
    claude = shutil.which("claude")
    if claude is None:
        console.print("[red]claude is not on PATH[/red]")
        raise typer.Exit(code=127)
    console.print(f"now: {have.transport or 'absent'} {have.target or ''}".rstrip())
    commands = ks.connect_commands(claude)
    if have.transport is None:
        commands = commands[1:]
    for argv in commands:
        console.print(f"  {' '.join(argv)}", highlight=False)
    if not apply:
        console.print("[dim]dry run: pass --apply to change ~/.claude.json[/dim]")
        return
    for argv in commands:
        ran = proc.run(argv, timeout=60, scope="rig.obsidian.knowledge_server")
        if not ran.ok:
            console.print(f"[red]{' '.join(argv[1:])} failed[/red]: {ran.err.strip() or ran.out.strip()}")
            raise typer.Exit(code=1)
    console.print("[green]connected[/green]; new sessions use the shared server")


@server_app.command("status")
def status_command() -> None:
    """Whether the server is installed, running and healthy, and whether Claude Code uses it."""
    installed = ks.installed_version()
    rows = [
        ("package", installed == ks.VERSION, f"{installed or 'not installed'} (pinned {ks.VERSION})"),
        ("LaunchAgent", ks.service_installed() and ks.service_loaded(), str(ks.plist_path())),
        ("health", ks.healthy(), f"{ks.editor_url()}healthz"),
    ]
    have = ks.current_registration()
    rows.append(("claude entry", have.shared, f"{have.transport or 'absent'} {have.target or ''}".rstrip()))
    for name, ok, detail in rows:
        mark = "[green]ok[/green]  " if ok else "[red]fail[/red]"
        console.print(f"{mark} {name:<13} {detail}", highlight=False)
    if not all(ok for _, ok, _ in rows):
        console.print("fix: `vault knowledge-server install`, then `vault knowledge-server connect --apply`")
        raise typer.Exit(code=UNHEALTHY_EXIT)
