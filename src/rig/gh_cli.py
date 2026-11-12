"""The `rig gh` command group: what the GitHub CLI cannot do on its own.

`pr` (`rig.gh_pr`) waits for a pull request to land and stays. `threads` (`rig.gh_threads`) is
temporary: drop its mount here when that package is retired.
"""

from __future__ import annotations

import typer

from rig.gh_pr.cli import make_app as make_pr_app
from rig.gh_threads.cli import make_app as make_threads_app

app = typer.Typer(no_args_is_help=True, help="Fill gaps in the GitHub CLI: land pull requests, handle review threads.")
app.add_typer(make_pr_app(), name="pr")
app.add_typer(make_threads_app(), name="threads")  # TEMPORARY: retire with rig.gh_threads
