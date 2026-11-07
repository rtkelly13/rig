"""Disk that regenerates from a command.

Measured across this estate: 14 GB, of which `temp/` is 9.2 GB, `node_modules`
2.7 GB and `.venv` 1.4 GB. Every byte of it comes back from `pnpm install`,
`uv sync`, or a rebuild — which is what makes it safe to delete and pointless to
back up.

Two rules keep it safe.

**`temp/` is not homogeneous.** The convention puts git worktrees at
`<repo>/temp/<name>`, alongside scratch files, so the largest reclaim target is
also where live checkouts live. Every `temp/` entry is classified before
anything is removed, and a registered worktree is handed to
`estate.cleanup`, never to `rmtree`.

**Nothing is removed from a clone holding irreplaceable state**, unless forced.
Freeing 3.7 GB is worth nothing next to losing an uncommitted afternoon, and the
two live in the same directory tree.
"""

from __future__ import annotations

import pathlib
from dataclasses import dataclass, field

from rig.estate.disksize import du


@dataclass(frozen=True)
class Category:
    """A kind of regenerable directory, and how it comes back."""

    name: str
    #: Directory names, matched at the repo root and one level below it.
    globs: tuple[str, ...]
    regenerate: str
    #: True when entries may be live checkouts and need classifying first.
    may_hold_worktrees: bool = False


CATEGORIES: tuple[Category, ...] = (
    Category("temp", ("temp",), "recreated as needed; worktrees via `git worktree add`", may_hold_worktrees=True),
    Category("node_modules", ("node_modules",), "pnpm install"),
    Category("python venv", (".venv", "venv"), "uv sync"),
    Category("js build output", ("dist", "build", ".next", "storybook-static", ".turbo"), "pnpm build"),
    Category("rust/dotnet build output", ("target", "obj", "bin"), "cargo build / dotnet build"),
    Category(
        "tool caches",
        ("__pycache__", ".pytest_cache", ".ruff_cache", ".mypy_cache", "coverage"),
        "regenerated on next run",
    ),
)

#: Below this, an entry is noise in a report about gigabytes.
REPORT_FLOOR = 20 * 1024 * 1024


@dataclass(frozen=True)
class Reclaimable:
    """One directory that could go."""

    path: pathlib.Path
    category: str
    bytes_used: int
    regenerate: str
    is_worktree: bool = False

    @property
    def safe_to_rmtree(self) -> bool:
        """True when plain removal is correct — a worktree needs git to do it."""
        return not self.is_worktree


def _no_entries() -> list[Reclaimable]:
    """Typed factory — see `estate.checks`."""
    return []


@dataclass
class RepoReclaim:
    """What one clone is holding that regenerates."""

    repo: pathlib.Path
    entries: list[Reclaimable] = field(default_factory=_no_entries)

    @property
    def total(self) -> int:
        return sum(entry.bytes_used for entry in self.entries)

    def by_category(self) -> dict[str, int]:
        totals: dict[str, int] = {}
        for entry in self.entries:
            totals[entry.category] = totals.get(entry.category, 0) + entry.bytes_used
        return dict(sorted(totals.items(), key=lambda kv: -kv[1]))


def _worktree_paths(repo: pathlib.Path) -> set[pathlib.Path]:
    """Return resolved paths of worktrees other than the main checkout."""
    from rig.estate.checks import list_worktrees

    out: set[pathlib.Path] = set()
    for tree in list_worktrees(repo):
        if tree.is_main:
            continue
        try:
            out.add(tree.path.resolve())
        except OSError:
            continue
    return out


def scan_repo(repo: pathlib.Path, *, floor: int = REPORT_FLOOR) -> RepoReclaim:
    """Find regenerable directories in *repo*, at the root and one level down.

    One level down catches the monorepo shape — `packages/*/node_modules` — while
    still refusing to walk the whole tree, which on a 5 GB checkout is the cost
    this command exists to avoid.
    """
    result = RepoReclaim(repo=repo)
    worktrees = _worktree_paths(repo)

    for category in CATEGORIES:
        for pattern in category.globs:
            for match in [*repo.glob(pattern), *repo.glob(f"*/{pattern}")]:
                if not match.is_dir() or match.is_symlink():
                    continue
                if category.may_hold_worktrees:
                    result.entries.extend(_explode_temp(match, worktrees, category, floor))
                    continue
                size = du(match)
                if size >= floor:
                    result.entries.append(
                        Reclaimable(
                            path=match,
                            category=category.name,
                            bytes_used=size,
                            regenerate=category.regenerate,
                        )
                    )

    result.entries.sort(key=lambda e: -e.bytes_used)

    # Catch registered worktrees residing outside the clone (e.g. <estate-root>/temp/)
    from rig.estate.checks import list_worktrees

    for tree in list_worktrees(repo):
        if tree.is_main:
            continue
        try:
            resolved = tree.path.resolve()
            if repo.resolve() not in resolved.parents and resolved.is_dir():
                size = du(resolved)
                if size >= floor:
                    result.entries.append(
                        Reclaimable(
                            path=tree.path,
                            category="external worktree",
                            bytes_used=size,
                            regenerate="git worktree add",
                            is_worktree=True,
                        )
                    )
        except OSError:
            continue

    result.entries.sort(key=lambda e: -e.bytes_used)
    return result


def _explode_temp(
    temp: pathlib.Path,
    worktrees: set[pathlib.Path],
    category: Category,
    floor: int,
) -> list[Reclaimable]:
    """Report `temp/` per child rather than as one lump.

    A single 3.7 GB figure for `temp/` is not actionable, because some of it is
    live worktrees and some is scratch. Per child, each entry can be decided —
    and the worktree ones route to `git worktree remove`.
    """
    out: list[Reclaimable] = []
    try:
        children = sorted(temp.iterdir())
    except OSError:
        return out
    for child in children:
        if child.is_symlink():
            continue
        size = du(child)
        if size < floor:
            continue
        try:
            is_tree = child.resolve() in worktrees
        except OSError:
            is_tree = False
        out.append(
            Reclaimable(
                path=child,
                category=category.name,
                bytes_used=size,
                regenerate="git worktree add" if is_tree else category.regenerate,
                is_worktree=is_tree,
            )
        )
    return out


def human(size: int) -> str:
    """Format bytes at one decimal place, for a table."""
    value = float(size)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if value < 1024 or unit == "TB":
            return f"{value:.0f}{unit}" if unit == "B" else f"{value:.1f}{unit}"
        value /= 1024
    return f"{value:.1f}TB"
