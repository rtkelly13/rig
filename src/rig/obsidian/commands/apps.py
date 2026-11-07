"""The graph command groups, created here so `graph` can register onto them without importing the package."""

import typer

graph_app = typer.Typer(help="[bold red]Graph[/bold red]: Manage link health, orphans, and vault structure.")
graph_links_app = typer.Typer(help="Broken-link discovery and repair commands.")
graph_metrics_app = typer.Typer(help="Vault density and folder health reporting.")
graph_orphans_app = typer.Typer(help="Orphan discovery and linking commands.")
graph_repair_app = typer.Typer(help="Metadata, filename and structure repair commands.")
