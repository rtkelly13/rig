"""CLI interface for managing the personal virtual monorepo estate.

Enforces ADR 0017: Nothing exists in the personal folder without the CLI managing it.
"""

from __future__ import annotations

import typer

from rig import csharp_cli
from rig.workspace.orca_cli import orca_cli

app = typer.Typer(
    name="workspace",
    help="Virtual monorepo estate management for the personal folder.",
    no_args_is_help=True,
    add_completion=False,
)
csharp_cli.add_stub(app, "skills")
csharp_cli.add_stub(app, "policy")
csharp_cli.add_group_stubs(app, "workspace")
app.add_typer(orca_cli, name="orca")


def main() -> None:
    app()


if __name__ == "__main__":
    main()
