"""``uv run mail``: check the Gmail rules file, export filters, plan sweeps, measure coverage."""

from __future__ import annotations

from pathlib import Path
from typing import Annotated

import typer
from rich.console import Console
from rich.table import Table

from rig.mail.apps_script import apps_script
from rig.mail.coverage import coverage, coverage_json, read_sample
from rig.mail.export import api_filters, filters_xml, sweep_json, sweep_plan
from rig.mail.rules import RulesError, RuleSet, default_rules_path, load_rules, match_query

app = typer.Typer(
    help=(
        "Gmail triage rules as code. Nothing here calls Gmail: filters are imported by hand "
        "and sweeps run through the Gmail connector (see the gmail-triage skill in the vault)."
    ),
    no_args_is_help=True,
)
console = Console()

RulesOption = Annotated[
    Path | None,
    typer.Option("--rules", help="Rules YAML. Defaults to $MAIL_RULES, else ~/code/personal/rig/temp/mail/rules.yaml."),
]


def _load(rules: Path | None) -> RuleSet:
    try:
        return load_rules(rules or default_rules_path())
    except RulesError as error:
        console.print(f"[red]error:[/red] {error}")
        raise typer.Exit(1) from error


@app.command()
def check(rules: RulesOption = None) -> None:
    """Validate the rules file and show each rule's Gmail query."""
    ruleset = _load(rules)
    table = Table(title=f"{len(ruleset.rules)} rules")
    table.add_column("Rule", style="cyan")
    table.add_column("Query")
    table.add_column("Actions")
    table.add_column("Filter", justify="center")
    for rule in ruleset.rules:
        acts = rule.actions
        names = [f"label:{acts.label}"] if acts.label else []
        names += [n for n, on in (("archive", acts.archive), ("read", acts.mark_read), ("star", acts.star)) if on]
        names += ["trash"] if acts.trash else []
        if acts.important is not None:
            names.append("important" if acts.important else "not-important")
        sweep_only = f"sweep only (older_than:{rule.older_than})"
        table.add_row(rule.name, match_query(rule.match), ", ".join(names), "yes" if rule.exportable else sweep_only)
    console.print(table)


@app.command()
def filters(
    rules: RulesOption = None,
    output: Annotated[Path | None, typer.Option("--output", "-o", help="Write the XML here instead of stdout.")] = None,
) -> None:
    """Export rules as Gmail filter XML (Settings → Filters → Import filters)."""
    xml = filters_xml(_load(rules))
    if output is None:
        typer.echo(xml, nl=False)
        return
    _ = output.write_text(xml, encoding="utf-8")
    console.print(f"wrote {output.as_posix()}")


@app.command()
def sweep(
    rules: RulesOption = None,
    rule: Annotated[str | None, typer.Option("--rule", help="Only this rule.")] = None,
) -> None:
    """Print the sweep plan as JSON: per rule, the query to search and the actions to apply."""
    steps = sweep_plan(_load(rules), rule)
    if rule is not None and not steps:
        console.print(f"[red]error:[/red] no rule named {rule!r}")
        raise typer.Exit(1)
    typer.echo(sweep_json(steps))


@app.command("apps-script")
def apps_script_cmd(
    rules: RulesOption = None,
    output: Annotated[Path | None, typer.Option("--output", "-o", help="Write here, not stdout.")] = None,
) -> None:
    """Export a Google Apps Script that sweeps in bulk and creates the filters (no Cloud project needed).

    The script embeds real queries: write it under temp/, never into a tracked file.
    """
    ruleset = _load(rules)
    script = apps_script(sweep_plan(ruleset), api_filters(ruleset))
    if output is None:
        typer.echo(script, nl=False)
        return
    _ = output.write_text(script, encoding="utf-8")
    console.print(f"wrote {output.as_posix()}")


@app.command("coverage")
def coverage_cmd(
    samples: Annotated[list[Path], typer.Argument(help="Saved search_threads JSON pages.", exists=True)],
    rules: RulesOption = None,
    as_json: Annotated[bool, typer.Option("--json", help="JSON output.")] = False,
    unclaimed: Annotated[bool, typer.Option("--unclaimed", help="Only senders rules miss.")] = False,
) -> None:
    """Show which senders in a saved inbox sample the rules claim, busiest first."""
    rows = coverage(_load(rules), read_sample(samples))
    if unclaimed:
        rows = [row for row in rows if not row.fully_claimed]
    if as_json:
        typer.echo(coverage_json(rows))
        return
    total = sum(row.threads for row in rows)
    claimed = sum(row.claimed for row in rows)
    table = Table(title=f"{claimed}/{total} sampled threads claimed")
    table.add_column("Threads", justify="right")
    table.add_column("Sender", style="cyan")
    table.add_column("Rule")
    table.add_column("Unclaimed example")
    for row in rows:
        label = ", ".join(row.rules) + (" ~" if row.approximate else "")
        if not row.fully_claimed:
            label = f"[yellow]{row.claimed}/{row.threads}[/yellow] {label}".rstrip()
        table.add_row(str(row.threads), row.sender, label, row.unclaimed_subject or "")
    console.print(table)


def main() -> None:
    """Console-script entry point."""
    app()
