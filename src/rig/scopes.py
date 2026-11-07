"""Which folders and GitHub owners make up the personal estate, and which hold work.

Nothing here is a naming convention; each list is configuration:

- **Personal** is tracked in ``config/estate-scopes.json``: the GitHub accounts whose repos
  are cloned straight into ``personal/`` (``owners``), and the GitHub orgs whose repos sit in
  a sibling folder of ``personal/`` named for the org (``orgs``, spelled as the folder is).
- **Work** differs between machines, so it lives in ``~/.workspace/config.json`` under
  ``tools.estate.work_dirs`` (see `rig.workspace_config`). Unset means no work scope.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from functools import cache
from pathlib import Path

from pydantic import StrictStr

from rig import roots
from rig.typed_json import decode_json
from rig.workspace_config import load_work_dirs, load_workspace_repo

SCOPES_FILE = "estate-scopes.json"


@dataclass(frozen=True, slots=True)
class Personal:
    #: GitHub accounts whose repos are cloned into ``personal/``.
    owners: tuple[StrictStr, ...] = ()
    #: GitHub orgs whose repos sit in ``<code root>/<org>``, spelled as the folder is.
    orgs: tuple[StrictStr, ...] = ()


@dataclass(frozen=True, slots=True)
class _Scopes:
    personal: Personal = field(default_factory=Personal)


@cache
def personal(config: Path | None = None) -> Personal:
    """The personal scope from ``config/estate-scopes.json``; empty when the file is missing.

    A file that is present but malformed raises: it is tracked, so that is a mistake to fix.
    """
    path = config or roots.checkout_config(SCOPES_FILE)
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return Personal()
    return decode_json(_Scopes, text).personal


def org_dirs(personal_dir: Path | None = None) -> list[Path]:
    """The org folders beside *personal_dir* (default: `roots.personal_dir`), whether or not they exist yet."""
    base = personal_dir or roots.personal_dir()
    return [base.parent / org for org in personal().orgs]


def repo_parent_dirs(personal_dir: Path | None = None) -> list[Path]:
    """Every folder whose children are personal clones: the personal folder, then each org folder."""
    base = personal_dir or roots.personal_dir()
    return [base, *org_dirs(base)]


def org_folder(owner: str) -> str | None:
    """The org folder for a GitHub *owner* (any case), or None when it is not a personal org."""
    return next((org for org in personal().orgs if org.casefold() == owner.casefold()), None)


def estate_owners() -> frozenset[str]:
    """Every GitHub account and org whose repos are the personal estate, lowercased."""
    scope = personal()
    return frozenset(owner.casefold() for owner in (*scope.owners, *scope.orgs))


def is_estate_owner(owner: str) -> bool:
    """Whether a GitHub *owner* (any case) is part of the personal estate."""
    return owner.casefold() in estate_owners()


def work_dirs(code_root: Path | None = None, config: Path | None = None) -> tuple[Path, ...]:
    """This machine's work folders: names resolve under *code_root*, absolute paths stand as written."""
    base = code_root or roots.code_root()
    return tuple(base / Path(entry).expanduser() for entry in load_work_dirs(config))


def workspace_repo(code_root: Path | None = None, config: Path | None = None) -> Path | None:
    """This machine's work workspace checkout, resolved like a work folder; None when unset."""
    configured = load_workspace_repo(config)
    if configured is None:
        return None
    return (code_root or roots.code_root()) / Path(configured).expanduser()
