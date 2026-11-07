"""`otel-stack`: rig's local OpenTelemetry collector (always on, rotated files).

`otel-stack clickhouse` is the on-demand half (`cli_clickhouse`): ClickHouse and the
contrib collector, started by `up` and stopped by `down`; nothing in the
always-on half depends on it.
"""

from __future__ import annotations

import datetime as dt
import sys

import typer
from rich.console import Console

from rig.otel_stack import cli_clickhouse, collector, retention, runtime
from rig.otel_stack.collector_config import GRPC_PORT, HTTP_PORT
from rig.otel_stack.paths import config_path, data_dir, log_path

app = typer.Typer(
    name="otel-stack",
    help="Rig's local OpenTelemetry: an always-on collector writing rotated files (7 days), and ClickHouse on demand.",
    no_args_is_help=True,
    add_completion=False,
)
collector_app = typer.Typer(help="The always-on core collector.", no_args_is_help=True)
service_app = typer.Typer(help="The macOS LaunchAgent that keeps it running.", no_args_is_help=True)
forward_app = typer.Typer(
    help="Optionally also send everything to another OTLP/HTTP collector, such as a Docker one.", no_args_is_help=True
)
app.add_typer(collector_app, name="collector")
app.add_typer(cli_clickhouse.app, name="clickhouse")
collector_app.add_typer(service_app, name="service")
collector_app.add_typer(forward_app, name="forward")
console = Console()


#: `ensure` and `start` exit with this when another process holds the collector's port.
FOREIGN_LISTENER_EXIT = 3


def _refuse_foreign_listener() -> None:
    """Exit when something other than the core collector holds the HTTP port; return when it is ours or unknown."""
    holder = runtime.owner(HTTP_PORT)
    if holder is None or holder.ours:
        return
    console.print(
        f"[yellow]port {HTTP_PORT} is held by {holder.command or 'an unknown process'} (pid {holder.pid}), "
        "not otelcol[/yellow]: free the port (for a Docker collector, publish it on another port and "
        "`otel-stack collector forward set` to it), then run this again"
    )
    raise typer.Exit(code=FOREIGN_LISTENER_EXIT)


def _darwin_service_loaded() -> bool:
    if sys.platform != "darwin":
        return False
    from rig.otel_stack import service

    return service.loaded()


@collector_app.command("install")
def install_command() -> None:
    """Download the pinned otelcol, verify its sha256, unpack the binary. Idempotent."""
    path = collector.install()
    console.print(f"[green]otelcol {collector.VERSION}[/green] verified at {path}")


@collector_app.command("config")
def config_command(show: bool = typer.Option(False, "--print", help="Also print it")) -> None:
    """Write the collector config."""
    path = runtime.write_config()
    console.print(f"wrote {path}")
    if show:
        console.print(path.read_text(encoding="utf-8"), markup=False, highlight=False)


@collector_app.command("start")
def start_command() -> None:
    """Start the collector now (via its LaunchAgent when one is loaded)."""
    if runtime.listening(HTTP_PORT):
        _refuse_foreign_listener()
        console.print(f"already listening on {HTTP_PORT}")
        return
    if _darwin_service_loaded():
        from rig import launchd
        from rig.otel_stack import service

        _ = runtime.write_config()
        launchd.kickstart(service.LABEL, restart=True)
        console.print("kicked the LaunchAgent")
        return
    if not collector.verified():
        console.print("[red]not installed[/red]: run `otel-stack collector install`")
        raise typer.Exit(code=1)
    pid = runtime.start(collector.binary_path())
    console.print(f"started pid {pid}; log {log_path()}")


@forward_app.command("set")
def forward_set_command(
    endpoint: str = typer.Argument(..., help="OTLP/HTTP base URL, e.g. http://127.0.0.1:24318"),
) -> None:
    """Also send everything to *endpoint* (a Docker collector, say). The files stay the record."""
    try:
        recorded = runtime.set_forward(endpoint)
    except ValueError as error:
        console.print(f"[red]{error}[/red]")
        raise typer.Exit(code=2) from error
    console.print(
        f"forwarding to {recorded}; restart the collector to apply (`collector stop`, then `collector start`)"
    )


@forward_app.command("off")
def forward_off_command() -> None:
    """Stop forwarding; only the files are written."""
    _ = runtime.set_forward(None)
    console.print("forwarding off; restart the collector to apply (`collector stop`, then `collector start`)")


@forward_app.command("show")
def forward_show_command() -> None:
    """Print the forward endpoint, or `off`."""
    console.print(runtime.forward() or "off")


@collector_app.command("stop")
def stop_command() -> None:
    """Stop it. On macOS this unloads the agent until `ensure` or `service install` loads it again."""
    if _darwin_service_loaded():
        from rig import launchd
        from rig.otel_stack import service

        launchd.unload(service.plist_path())
        console.print("unloaded the LaunchAgent")
        return
    console.print("stopped" if runtime.stop_recorded() else "no collector started by `otel-stack` is recorded")


@collector_app.command("ensure")
def ensure_command() -> None:
    """Install if missing, prune rotated files past 7 days, and make sure it is listening. Safe to run every tick."""
    binary = collector.install()
    _ = runtime.write_config()
    pruned = retention.prune(data_dir(), now=dt.datetime.now(dt.UTC), apply=True)
    if pruned:
        console.print(f"pruned {len(pruned)} rotated files older than {retention.KEEP_DAYS} days")
    if sys.platform == "darwin":
        from rig import launchd
        from rig.otel_stack import service

        if service.installed():
            if not service.loaded():
                _ = service.install(binary, config_path())
                console.print("loaded the LaunchAgent")
            elif not runtime.listening(HTTP_PORT):
                launchd.kickstart(service.LABEL, restart=True)
                console.print("restarted the LaunchAgent")
            else:
                _refuse_foreign_listener()
                console.print(f"listening on {HTTP_PORT}")
            return
    if runtime.listening(HTTP_PORT):
        _refuse_foreign_listener()
        console.print(f"listening on {HTTP_PORT}")
        return
    pid = runtime.start(binary)
    console.print(f"started pid {pid}")


@collector_app.command("status")
def status_command() -> None:
    """Install state, ports, process memory, and what the data folder holds."""
    installed = collector.binary_path().is_file()
    verified = collector.verified()
    console.print(
        f"binary: {'installed' if installed else 'missing'} otelcol {collector.VERSION} "
        f"({'sha256 verified' if verified else 'not verified'}) {collector.binary_path()}"
    )
    if sys.platform == "darwin":
        from rig.otel_stack import service

        state = "loaded" if service.loaded() else ("installed, not loaded" if service.installed() else "not installed")
        console.print(f"service: {state} ({service.plist_path()})")
    for port in (GRPC_PORT, HTTP_PORT):
        if not runtime.listening(port):
            console.print(f"port {port}: closed")
            continue
        holder = runtime.owner(port)
        if holder is None:
            console.print(f"port {port}: listening")
        elif holder.ours:
            console.print(f"port {port}: listening (otelcol, pid {holder.pid})")
        else:
            name = holder.command or "unknown process"
            console.print(f"port {port}: [yellow]held by {name} (pid {holder.pid}), not otelcol[/yellow]")
    forward = runtime.forward()
    console.print(f"forward: {forward or 'off'}")
    holder = runtime.owner(HTTP_PORT)
    pid = runtime.listener_pid(HTTP_PORT) if holder is None or holder.ours else None
    if pid is not None:
        mb = runtime.resident_mb(pid)
        console.print(f"process: pid {pid}" + (f", {mb:.1f} MB resident" if mb is not None else ""))
    files = sorted(path for path in data_dir().glob("*.jsonl")) if data_dir().is_dir() else []
    if not files:
        console.print(f"data: empty ({data_dir()})")
        return
    size = sum(path.stat().st_size for path in files)
    stamps = sorted(path.stat().st_mtime for path in files)
    oldest, newest = _local(stamps[0]), _local(stamps[-1])
    console.print(f"data: {len(files)} files, {size / 1_048_576:.1f} MB, {oldest} to {newest} ({data_dir()})")


def _local(stamp: float) -> str:
    return dt.datetime.fromtimestamp(stamp, tz=dt.UTC).astimezone().strftime("%Y-%m-%d %H:%M")


@service_app.command("install")
def service_install_command() -> None:
    """Install and load the LaunchAgent (macOS). Installs the binary first if needed."""
    if sys.platform != "darwin":
        console.print("[yellow]Only on macOS; on Windows the jobs coordinator runs `collector ensure`.[/yellow]")
        raise typer.Exit(code=1)
    from rig.otel_stack import service

    binary = collector.install()
    path = service.install(binary, runtime.write_config())
    console.print(f"[green]loaded[/green] {path}")


@service_app.command("uninstall")
def service_uninstall_command() -> None:
    """Unload and remove the LaunchAgent."""
    if sys.platform != "darwin":
        console.print("Not installed.")
        return
    from rig.otel_stack import service

    console.print("[green]removed[/green]" if service.uninstall() else "Not installed.")


def main() -> None:
    app()
