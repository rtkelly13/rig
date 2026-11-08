"""Git for tests: the real binary against throwaway repositories, never a mock.

Git is cheap and fast enough to run for real, so its stand-in is a temp clone with a bare origin rather
than a fake class (docs/target-architecture.md, "Behaviour suites share one vocabulary").
"""

from __future__ import annotations

import pathlib

from rig import proc


def git(args: list[str], cwd: pathlib.Path) -> str:
    """Run one git command through `rig.proc` and return its stdout; a non-zero exit raises `ProcError`."""
    return proc.git(*args, cwd=cwd, check=True).out.rstrip()


def clone_with_origin(tmp_path: pathlib.Path) -> pathlib.Path:
    """`tmp_path/clone`, with `main` pushed to a bare `tmp_path/origin.git`.

    The first commit holds `README.md` and `gone.txt` and ignores `.env` and `node_modules/`.
    """
    origin = tmp_path / "origin.git"
    _ = git(["init", "-q", "--bare", "-b", "main", str(origin)], tmp_path)
    root = tmp_path / "clone"
    root.mkdir()
    _ = git(["init", "-q", "-b", "main"], root)
    _ = git(["config", "user.email", "t.user@example.com"], root)
    _ = git(["config", "user.name", "T"], root)
    _ = git(["config", "core.autocrlf", "false"], root)
    _ = git(["remote", "add", "origin", str(origin)], root)
    _ = (root / ".gitignore").write_text(".env\nnode_modules/\n", encoding="utf-8")
    _ = (root / "README.md").write_text("hi\n", encoding="utf-8")
    _ = (root / "gone.txt").write_text("delete me\n", encoding="utf-8")
    _ = git(["add", "."], root)
    _ = git(["commit", "-qm", "first"], root)
    _ = git(["push", "-q", "-u", "origin", "main"], root)
    return root
