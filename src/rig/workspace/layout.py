"""Where each managed project lives on disk.

Repos owned by the personal account sit directly in the estate root
(``~/code/personal/<name>``). Repos owned by one of the estate's own GitHub
orgs (``config/estate-scopes.json``) sit in a sibling folder named for the org
(``~/code/<org>/<name>``), so the
folder tree mirrors GitHub ownership. Anything else (forks, reference clones)
stays in the estate root.
"""

from __future__ import annotations

import pathlib

from rig import scopes
from rig.repo_slug import RepoSlug


def org_folder(repo: str) -> str | None:
    """The org folder a repo belongs in, or None when it belongs in the estate root."""
    slug = RepoSlug.from_remote(repo)
    if slug is None:
        return None
    return scopes.org_folder(slug.owner)


def project_dir(estate_dir: pathlib.Path, name: str, repo: str) -> pathlib.Path:
    """The directory a managed project is expected to be cloned into."""
    org = org_folder(repo)
    return estate_dir.parent / org / name if org else estate_dir / name
