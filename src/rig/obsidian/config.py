"""Vault path resolution.

The vault exists twice: the Google Drive copy that Obsidian opens and the phone
edits, and the git copy that is the record. **Maintenance targets the git copy** —
repairs there are reviewable and committable, and the two converge by running the
`vault-reconcile` skill rather than by continuous sync.

Override with `VAULT_PATH` (or the legacy `TVS_VAULT_PATH`).
"""

from __future__ import annotations

import os
from pathlib import Path

# The record. Repairs land here, get reviewed, and reach Drive via reconciliation.
DEFAULT_VAULT_PATH = Path.home() / "code" / "personal" / "obsidian-vault" / "Vault"

# What Obsidian opens. Reported by `vault paths`; never written to by maintenance.
DEFAULT_DRIVE_VAULT_PATH = Path.home() / "Google Drive" / "My Drive" / "Obsidian" / "Vault"

_ENV_VARS = ("VAULT_PATH", "TVS_VAULT_PATH")


def get_vault_path() -> Path:
    """The vault maintenance operates on, honouring an env override."""
    for var in _ENV_VARS:
        value = os.environ.get(var)
        if value:
            return Path(value).expanduser()
    return DEFAULT_VAULT_PATH


def get_drive_vault_path() -> Path:
    """The Google Drive copy, for reconciliation and reporting."""
    value = os.environ.get("DRIVE_VAULT_PATH")
    return Path(value).expanduser() if value else DEFAULT_DRIVE_VAULT_PATH


class Config:
    """Resolved paths for a maintenance run.

    Deliberately a plain object rather than pydantic settings: one value is read
    from the environment, and a settings framework for that is more machinery than
    the problem needs.
    """

    def __init__(self, vault_path: Path | None = None) -> None:
        self.vault_path: Path = vault_path or get_vault_path()
        self.drive_vault_path: Path = get_drive_vault_path()

    # Back-compat with the ported call sites.
    @property
    def tvs_vault_path(self) -> Path:
        return self.vault_path
