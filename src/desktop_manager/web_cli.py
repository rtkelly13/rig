"""CLI subcommands for the Rig web dashboard, background services, and SQLite database."""

from __future__ import annotations

import webbrowser
from typing import TYPE_CHECKING

import typer
from rich.console import Console
from rich.table import Table

if TYPE_CHECKING:
    from rig.web_routes import Route

console = Console()


def mounted_routes() -> tuple[Route, ...]:
    """The JSON routes features contribute to `rig web`.

    This is the composition root, so it is the one place that may import several features at once; the server
    itself imports none of them.
    """
    from desktop_manager.jobs.web import JobsRoute
    from rig.estate.web import make_route as estate_route
    from rig.otel_stack.web import telemetry_route

    return (telemetry_route, estate_route(), JobsRoute())


web_app = typer.Typer(
    name="web",
    help="Rig web dashboard & visual studio (static site + Python API).",
    invoke_without_command=True,
    add_completion=False,
)


@web_app.callback()
def web_main(
    ctx: typer.Context,
    port: int = typer.Option(8090, "--port", "-p", help="Port to bind the web server to."),
    host: str = typer.Option("127.0.0.1", "--host", "-h", help="Host address to bind to."),
    open_browser: bool = typer.Option(True, "--open/--no-open", help="Open in browser on launch."),
    preview: bool = typer.Option(False, "--preview", help="Run as preview environment (defaults to port 8091)."),
) -> None:
    """Launch the persistent rig web dashboard & visual studio."""
    if ctx.invoked_subcommand is not None:
        return

    effective_port = 8091 if (preview and port == 8090) else port
    from rig.image_drift.server import StudioServer

    server = StudioServer(host=host, port=effective_port, is_preview=preview, routes=mounted_routes())
    url = f"http://{host}:{effective_port}"
    mode_str = "[bold magenta][PREVIEW MODE][/bold magenta]" if preview else "[bold green][PROD MODE][/bold green]"
    console.print(f"{mode_str} Rig Web Dashboard running at [cyan]{url}[/cyan]")
    console.print("[dim]Static site hosted with Python API · Low resource consumption[/dim]")

    if open_browser:
        webbrowser.open(url)

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        console.print("\n[yellow]Stopping server...[/yellow]")
        server.server_close()


@web_app.command(name="mcp")
def web_mcp_cmd() -> None:
    """Serve the ops pages' data to agents as an MCP server on stdio.

    Register it with: claude mcp add rig-ops -- uv run --directory <rig checkout> rig web mcp
    """
    from desktop_manager.web_mcp import create_server

    create_server().run("stdio")


@web_app.command(name="install")
def web_install_cmd(
    preview: bool = typer.Option(False, "--preview", help="Install preview background service on port 8091."),
    port: int | None = typer.Option(None, "--port", "-p", help="Explicit port override."),
) -> None:
    """Register and start the web dashboard as a background service (macOS LaunchAgent or Windows Task)."""
    from desktop_manager.service_installer import install_web_service

    result = install_web_service(preview=preview, port=port)
    console.print(result)


@web_app.command(name="ensure")
def web_ensure_cmd(
    port: int = typer.Option(8090, "--port", "-p", help="Port the dashboard answers on."),
    timeout: float = typer.Option(30.0, "--timeout", help="Seconds to wait for it to answer."),
) -> None:
    """Start the background dashboard service unless something already answers on the port.

    Exits 1 when nothing answers within the timeout. The Review desktop runs this before it opens the dashboard.
    """
    from desktop_manager.service_installer import ensure_web_service

    if not ensure_web_service(port=port, timeout_s=timeout):
        console.print(f"[red]Nothing answered on port {port} within {timeout:g}s.[/red]")
        raise typer.Exit(1)
    console.print(f"Dashboard answering on http://127.0.0.1:{port}")


@web_app.command(name="uninstall")
def web_uninstall_cmd(
    preview: bool = typer.Option(False, "--preview", help="Uninstall preview background service."),
) -> None:
    """Unload and remove the background web service."""
    from desktop_manager.service_installer import uninstall_web_service

    result = uninstall_web_service(preview=preview)
    console.print(result)


@web_app.command(name="backup")
def web_backup_cmd() -> None:
    """Perform an immediate SQLite database backup into backups/."""
    from rig.db.backup import backup_database

    backup_path = backup_database()
    console.print(f"[bold green]Database backup created:[/bold green] {backup_path}")


@web_app.command(name="migrate")
def web_migrate_cmd() -> None:
    """Run pending SQLite schema migrations."""
    from rig.db import default_db_path, get_db_connection, run_migrations

    conn = get_db_connection()
    try:
        applied = run_migrations(conn)
        console.print(f"[bold green]Migrations applied:[/bold green] {applied} on {default_db_path()}")
    finally:
        conn.close()


@web_app.command(name="db-status")
def web_db_status_cmd() -> None:
    """Display current SQLite schema version and applied migration history."""
    from rig.db import (
        current_schema_version,
        default_db_path,
        get_applied_migrations,
        get_db_connection,
    )

    db_path = default_db_path()
    conn = get_db_connection()
    try:
        version = current_schema_version(conn)
        records = get_applied_migrations(conn)

        console.print(f"[bold cyan]Database Path:[/bold cyan] {db_path}")
        console.print(f"[bold cyan]Current Schema Version:[/bold cyan] {version}")

        if not records:
            console.print("[yellow]No migrations have been recorded yet.[/yellow]")
            return

        table = Table(title="Applied Schema Migrations", header_style="bold magenta")
        table.add_column("Version", justify="right", style="cyan")
        table.add_column("Name", style="green")
        table.add_column("Applied At", style="dim")

        for r in records:
            table.add_row(str(r.version), r.name, r.applied_at)

        console.print(table)
    finally:
        conn.close()
