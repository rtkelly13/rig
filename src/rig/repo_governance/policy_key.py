"""The key a local checkout's policy is looked up by.

`PolicyConfig` matches profiles and overrides by bare repo name, and infers a profile from the
owner (``owner_profiles``) only when the key is an ``owner/name`` slug. So a checkout is keyed by
the most specific identity that can be read without a network call:

1. ``owner/name`` from ``remote.origin.url`` (git reads the shared config from a worktree too);
2. ``<org>/<folder>`` when the checkout sits in an org folder beside ``personal/`` (`rig.roots`);
3. the bare folder name.
"""

from __future__ import annotations

from collections.abc import Iterable
from pathlib import Path

from rig import proc, scopes
from rig.repo_slug import RepoSlug

__all__ = ("bare_name", "names_match", "policy_key", "policy_key_from")


def bare_name(key: str) -> str:
    """A key's last segment, casefolded: config lists bare names, and GitHub names ignore case."""
    return key.rsplit("/", 1)[-1].casefold()


def names_match(names: Iterable[str], key: str) -> bool:
    """True when *key* (bare or ``owner/name``) is one of the bare *names*, ignoring case."""
    bare = bare_name(key)
    return any(bare_name(name) == bare for name in names)


def policy_key_from(repo_path: Path, origin_url: str | None) -> str:
    """The policy key for *repo_path* given its origin URL (None or empty when it has none)."""
    slug = RepoSlug.from_remote(origin_url)
    if slug is not None:
        return slug.full_name
    parent = repo_path.parent.name
    org = scopes.org_folder(parent)
    return f"{org}/{repo_path.name}" if org else repo_path.name


def policy_key(repo_path: Path) -> str:
    """The policy key for the checkout at *repo_path*; see the module docstring.

    Only a folder that is itself a checkout (``.git`` dir, or file in a worktree) is asked for a
    remote: git would otherwise answer for an enclosing repo.
    """
    if not (repo_path / ".git").exists():
        return policy_key_from(repo_path, None)
    origin = proc.git_out("config", "--get", "remote.origin.url", cwd=repo_path, timeout=5)
    return policy_key_from(repo_path, origin or None)
