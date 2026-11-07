"""Repo health status — runs quality gates and summarizes results."""

from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path

import typer
from rich import box
from rich.console import Console
from rich.panel import Panel
from rich.table import Table

from rig import proc

app = typer.Typer(add_completion=False)
_console = Console()


@dataclass
class _Result:
    name: str
    passed: bool
    detail: str = ""


def _repo_root() -> Path:
    top = proc.git_out("rev-parse", "--show-toplevel")
    return Path(top) if top else Path.cwd()


@dataclass(frozen=True, slots=True)
class _Ran:
    ok: bool
    output: str


def _run(cmd: list[str], cwd: Path) -> _Ran:
    r = proc.git(*cmd[1:], cwd=cwd) if cmd[0] == "git" else proc.run(cmd, cwd=cwd)
    return _Ran(r.ok, (r.out + r.err).strip())


def _last_line(text: str) -> str:
    lines = [ln for ln in text.splitlines() if ln.strip()]
    return lines[-1][:100] if lines else ""


def _git_results(root: Path) -> list[_Result]:
    results: list[_Result] = []

    branch = _run(["git", "rev-parse", "--abbrev-ref", "HEAD"], root).output
    results.append(_Result("Branch", True, branch))

    porcelain = _run(["git", "status", "--porcelain"], root).output
    dirty = bool(porcelain.strip())
    first_change = porcelain.splitlines()[0].strip() if dirty else ""
    results.append(_Result("Clean worktree", not dirty, first_change if dirty else ""))

    ab = _run(["git", "rev-list", "--count", "--left-right", "HEAD...@{u}"], root).output
    if "\t" in ab:
        ahead, behind = ab.split("\t", 1)
        synced = ahead == "0" and behind == "0"
        results.append(_Result("Remote sync", synced, "in sync" if synced else f"+{ahead}/-{behind}"))

    return results


def _quality_results(root: Path) -> list[_Result]:
    checks: dict[str, list[str]] = {
        "Lockfile": ["uv", "lock", "--check"],
        "Lint": ["uv", "run", "ruff", "check", "src/", "--quiet"],
        "Format": ["uv", "run", "ruff", "format", "src/", "--check", "--quiet"],
        "Types": ["uv", "run", "basedpyright"],
        "ty": ["uv", "run", "ty", "check", "src", "packages/type-ratchet/src"],
        "Ratchet": ["uv", "run", "ratchet"],
        "Imports": ["uv", "run", "lint-imports"],
        "Security": ["uv", "run", "bandit", "-r", "src/", "-q", "-ll"],
    }

    results: list[_Result] = []
    for name, cmd in checks.items():
        ran = _run(cmd, root)
        results.append(_Result(name, ran.ok, "" if ran.ok else _last_line(ran.output)))

    return results


@app.command()
def status(
    tests: bool = typer.Option(False, "--tests", "-t", help="Also run the test suite."),
    ci_only: bool = typer.Option(False, "--ci", help="Run only tests marked @pytest.mark.ci."),
) -> None:
    """Check the repo is well-managed: git state, lint, types, and security."""
    root = _repo_root()

    all_results = _git_results(root) + _quality_results(root)

    if tests or ci_only:
        cmd = ["uv", "run", "pytest", "-q", "--tb=no"]
        if ci_only:
            cmd += ["-m", "ci"]
        ran = _run(cmd, root)
        all_results.append(_Result("Tests", ran.ok, _last_line(ran.output)))

    all_passed = all(r.passed for r in all_results)

    table = Table(box=box.ROUNDED, show_header=False, padding=(0, 1), expand=False)
    table.add_column("", width=3)
    table.add_column("Check", style="bold", no_wrap=True)
    table.add_column("Detail", style="dim")

    for r in all_results:
        icon = "[green]OK[/]" if r.passed else "[red]!![/]"
        table.add_row(icon, r.name, r.detail)

    border = "green" if all_passed else "red"
    title = "[green]Healthy[/]" if all_passed else "[red]Issues Found[/]"
    _console.print(Panel(table, title=title, border_style=border))

    sys.exit(0 if all_passed else 1)


def main() -> None:
    app()
