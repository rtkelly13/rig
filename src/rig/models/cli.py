"""Typer CLI interface for enumerating models across rig coding assistants."""

from __future__ import annotations

import json
from dataclasses import asdict

import typer
from rich.console import Console
from rich.table import Table

from .discovery import discover_all_models

app = typer.Typer(
    name="models",
    help="Enumerate AI models supported across coding agent CLIs in rig (OpenCode, Codex, Antigravity, Claude Code).",
    no_args_is_help=False,
)
console = Console()


@app.callback(invoke_without_command=True)
def list_models_cmd(
    agent: str | None = typer.Option(
        None,
        "--agent",
        "-a",
        help="Filter by agent CLI: codex, opencode, antigravity (agy), claude-code (claude), or copilot.",
    ),
    provider: str | None = typer.Option(
        None,
        "--provider",
        "-p",
        help="Filter by upstream model provider: openai, anthropic, google, deepseek, github-copilot, etc.",
    ),
    all_models: bool = typer.Option(
        False,
        "--all",
        help="Enumerate all discovered models across provider CLIs and caches instead of evaluated models only.",
    ),
    as_json: bool = typer.Option(
        False,
        "--json",
        help="Output models list as formatted JSON.",
    ),
) -> None:
    """Enumerate and display available models across installed agent CLIs."""
    models = discover_all_models(agent=agent, all_models=all_models)

    if provider:
        target_prov = provider.lower().strip()
        models = tuple(m for m in models if m.provider and target_prov in m.provider.lower())

    if as_json:
        payload = [asdict(m) for m in models]
        console.print(json.dumps(payload, indent=2), highlight=False, markup=False, soft_wrap=True)
        return

    if not models:
        console.print("[yellow]No models found matching the specified filters.[/yellow]")
        return

    title = (
        f"All Discovered AI Coding Models ({len(models)} models)"
        if all_models
        else f"AI Coding Models Evaluated in Rig ({len(models)} models)"
    )
    table = Table(
        title=title,
        show_header=True,
        header_style="bold cyan",
    )
    table.add_column("Agent / CLI", style="green", width=14)
    table.add_column("Model ID", style="bold white")
    table.add_column("Display Name", style="cyan")
    table.add_column("Provider", style="magenta", width=14)
    table.add_column("Effort / Levels", style="dim", width=22)

    for m in models:
        efforts = ", ".join(m.reasoning_levels) if m.reasoning_levels else "—"
        prov = m.provider or "—"
        table.add_row(m.agent, m.model_id, m.display_name, prov, efforts)

    console.print(table)


def main() -> None:
    app()


if __name__ == "__main__":
    main()
