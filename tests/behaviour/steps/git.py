"""Given-steps over a real clone from `tests.fakes.git.clone_with_origin`."""

from __future__ import annotations

import pathlib

from tests.fakes.git import git


def add_worktree(clone: pathlib.Path, branch: str, *, push: bool = True) -> pathlib.Path:
    """A linked worktree on a new `branch` under `.worktrees/`, pushed to origin unless `push` is false."""
    path = clone / ".worktrees" / branch
    _ = git(["worktree", "add", "-q", "-b", branch, str(path)], clone)
    if push:
        _ = git(["push", "-q", "-u", "origin", branch], path)
    return path


def commit(path: pathlib.Path, name: str, text: str) -> None:
    """One local commit adding `name` with `text`."""
    _ = (path / name).write_text(text, encoding="utf-8")
    _ = git(["add", name], path)
    _ = git(["commit", "-qm", f"add {name}"], path)


def make_dirty(path: pathlib.Path) -> None:
    """One change of each kind: modified `README.md`, staged `staged.txt`, deleted `gone.txt`, untracked `new.txt`."""
    _ = (path / "README.md").write_text("edited\n", encoding="utf-8")
    _ = (path / "staged.txt").write_text("staged\n", encoding="utf-8")
    _ = git(["add", "staged.txt"], path)
    (path / "gone.txt").unlink()
    _ = (path / "new.txt").write_text("untracked\n", encoding="utf-8")
