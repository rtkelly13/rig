"""Repo slugs for the estate's port and Vercel naming.

The package bindings in ``config/managed-projects.json`` are read in C# (``Rig.Core.Packages.PackageBindings``):
``rig workspace packages`` and the estate graph use them there.
"""

from __future__ import annotations

from rig.repo_slug import RepoSlug


def repo_slug(url: str) -> str | None:
    """``git@github.com:o/r.git``, ``git+https://github.com/o/r.git`` and friends -> ``o/r``."""
    slug = RepoSlug.from_remote(url)
    return slug.full_name if slug else None
