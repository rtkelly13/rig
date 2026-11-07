"""Find every platform-restricted CLI command and render the README table of them.

The console scripts in ``pyproject.toml`` are the roots. Each ``main`` entry point
runs one Typer app; walking its click command tree and reading
:func:`~rig.platforms.supported_platforms` off each callback lists the
commands that :func:`~rig.platforms.requires` gated, without reading
their bodies.

Regenerate the README table with ``uv run python -m rig.platform_table --write``.
"""

from __future__ import annotations

import argparse
import importlib
import sys
import tomllib
from collections.abc import Iterator, Sequence
from dataclasses import dataclass, field
from pathlib import Path

import click
import typer
import typer.main
from pydantic import StrictStr

from rig.platforms import Platform, platform_label, supported_platforms
from rig.typed_json import decode

REPO_ROOT = Path(__file__).resolve().parents[2]
START_MARKER = "<!-- platform-table:start -->"
END_MARKER = "<!-- platform-table:end -->"
REGENERATE_COMMAND = "uv run python -m rig.platform_table --write"


@dataclass(frozen=True)
class EntryPoint:
    """One ``[project.scripts]`` line: ``script = "module:function"``."""

    script: str
    target: str

    @property
    def module(self) -> str:
        return self.target.partition(":")[0]

    @property
    def function(self) -> str:
        return self.target.partition(":")[2]


@dataclass(frozen=True)
class CommandRecord:
    """A leaf command reached from an entry point, with the platforms it declared."""

    path: tuple[str, ...]
    command: click.Command
    platforms: tuple[Platform, ...] | None

    @property
    def invocation(self) -> str:
        return " ".join(self.path)


@dataclass(frozen=True, slots=True)
class _Project:
    scripts: dict[str, StrictStr] = field(default_factory=dict[str, StrictStr])


@dataclass(frozen=True, slots=True)
class _Pyproject:
    project: _Project = field(default_factory=_Project)


def load_entry_points(pyproject: Path | None = None) -> list[EntryPoint]:
    """The console scripts declared in ``pyproject.toml``, in file order."""
    path = pyproject or REPO_ROOT / "pyproject.toml"
    pyproject_file = decode(_Pyproject, tomllib.loads(path.read_text(encoding="utf-8")))
    return [EntryPoint(script=name, target=target) for name, target in pyproject_file.project.scripts.items()]


def resolve_app(entry: EntryPoint) -> typer.Typer | None:
    """The Typer app a ``main`` entry point runs; None for hand-rolled entry points.

    The root app is the module-level ``typer.Typer`` that no other module-level app
    mounts as a sub-group.
    """
    module = importlib.import_module(entry.module)
    if entry.function != "main":
        return None
    members = list[object](vars(module).values())
    apps = [value for value in members if isinstance(value, typer.Typer)]
    mounted = {id(group.typer_instance) for app in apps for group in app.registered_groups}
    roots = [app for app in apps if id(app) not in mounted]
    if len(roots) != 1:
        raise LookupError(f"{entry.target}: expected one root Typer app, found {len(roots)}")
    return roots[0]


def walk_commands(app: typer.Typer, prog: str) -> Iterator[CommandRecord]:
    """Every leaf command under ``app``, depth-first in name order."""
    yield from _walk(typer.main.get_command(app), (prog,))


def _walk(command: click.Command, path: tuple[str, ...]) -> Iterator[CommandRecord]:
    if isinstance(command, click.Group):
        for name in sorted(command.commands):
            yield from _walk(command.commands[name], (*path, name))
        return
    declared = supported_platforms(command.callback) if command.callback is not None else None
    yield CommandRecord(path=path, command=command, platforms=declared)


def restricted_commands(entry_points: Sequence[EntryPoint] | None = None) -> list[CommandRecord]:
    """Commands that declared a platform restriction, across every Typer entry point."""
    records: list[CommandRecord] = []
    for entry in entry_points if entry_points is not None else load_entry_points():
        app = resolve_app(entry)
        if app is None:
            continue
        records.extend(record for record in walk_commands(app, entry.script) if record.platforms is not None)
    return sorted(records, key=lambda record: record.path)


def render_table(records: Sequence[CommandRecord]) -> str:
    """A Markdown table of ``records``: command and the platforms it runs on."""
    lines = ["| Command | Platforms |", "| --- | --- |"]
    lines.extend(f"| `{record.invocation}` | {platform_label(record.platforms or ())} |" for record in records)
    return "\n".join(lines)


def splice_readme(readme: str, table: str) -> str:
    """``readme`` with the text between the platform-table markers replaced by ``table``."""
    start = readme.find(START_MARKER)
    end = readme.find(END_MARKER)
    if start == -1 or end == -1 or end < start:
        raise ValueError(f"README is missing the {START_MARKER} / {END_MARKER} markers")
    head = readme[: start + len(START_MARKER)]
    return f"{head}\n\n{table}\n\n{readme[end:]}"


class _Args(argparse.Namespace):
    write: bool = False
    readme: Path = REPO_ROOT / "README.md"


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Render the README table of platform-restricted commands.")
    parser.add_argument("--write", action="store_true", help="Rewrite the table in README.md.")
    parser.add_argument("--readme", type=Path, default=REPO_ROOT / "README.md")
    args = parser.parse_args(argv, namespace=_Args())
    table = render_table(restricted_commands())
    readme_path = args.readme
    if not args.write:
        sys.stdout.write(table + "\n")
        return 0
    current = readme_path.read_text(encoding="utf-8")
    updated = splice_readme(current, table)
    if updated != current:
        readme_path.write_text(updated, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
