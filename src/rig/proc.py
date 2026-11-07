"""The one way rig runs `git`, `gh` and other processes.

Every call goes through `rig.tracing.run`, so it is recorded as a span, and runs with stdin closed
(a caller speaking a protocol on stdio is safe), UTF-8 output with undecodable bytes replaced, and
no console window on Windows. Nothing raises for a failed or missing command unless ``check=True``:
the result says what happened.

Git calls whose working directory lies under ``<code root>/external`` or ``<code root>/other`` are
hardened automatically. Those folders hold code nobody here wrote or reviewed, and a clone's own
config can run programs: hooks, an fsmonitor command, ``ext::`` transports, submodule recursion. The
hardening turns each of those off for the one call, and stops git prompting for credentials. It does
not ban other protocols, so ``fetch`` still works.
"""

from __future__ import annotations

import os
import subprocess
import sys
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path
from typing import Protocol

from rig import roots, tracing
from rig.typed_json import try_decode_json

#: Folders under the code root whose clones are untrusted (see the code root's AGENTS.md).
UNTRUSTED_DIRS: tuple[str, ...] = ("external", "other")
#: Prepended to every git call under an untrusted folder.
HARDENING_ARGS: tuple[str, ...] = (
    "-c",
    f"core.hooksPath={os.devnull}",
    "-c",
    "core.fsmonitor=false",
    "-c",
    "protocol.ext.allow=never",
    "-c",
    "submodule.recurse=false",
)
HARDENING_ENV: Mapping[str, str] = {"GIT_TERMINAL_PROMPT": "0"}

GIT_TIMEOUT = 30.0
GH_TIMEOUT = 60.0
NOT_FOUND = 127
TIMED_OUT = 124


@dataclass(frozen=True, slots=True)
class Ran:
    """How a command ended. ``out`` and ``err`` are the captured text, unstripped."""

    code: int
    out: str
    err: str

    @property
    def ok(self) -> bool:
        return self.code == 0


class ProcError(Exception):
    """A command run with ``check=True`` did not exit 0."""

    argv: list[str]
    ran: Ran

    def __init__(self, argv: list[str], ran: Ran) -> None:
        self.argv = argv
        self.ran = ran
        detail = ran.err.strip() or ran.out.strip()
        super().__init__(f"{' '.join(argv)} exited {ran.code}" + (f": {detail}" if detail else ""))


def is_untrusted(path: Path | None) -> bool:
    """Whether *path* (default: the working directory) lies under an untrusted folder of the code root."""
    target = (path or Path.cwd()).resolve()
    root = roots.code_root().resolve()
    return any(target.is_relative_to(root / name) for name in UNTRUSTED_DIRS)


def run(
    argv: list[str],
    *,
    cwd: Path | None = None,
    timeout: float | None = None,
    env: Mapping[str, str] | None = None,
    check: bool = False,
    stdin_text: str | None = None,
    scope: str = "rig.proc",
) -> Ran:
    """Run *argv* and capture its output. *env* adds to the inherited environment rather than replacing it.

    stdin is closed unless *stdin_text* is given, which is written to it instead.

    A missing executable gives code 127 and a timeout code 124, as a shell would report them.
    """
    flags = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0
    try:
        done = tracing.run(
            argv,
            scope=scope,
            capture_output=True,
            timeout=timeout,
            cwd=cwd,
            env={**os.environ, **env} if env else None,
            stdin=subprocess.DEVNULL if stdin_text is None else None,
            input_text=stdin_text,
            encoding="utf-8",
            errors="replace",
            creationflags=flags,
        )
        ran = Ran(done.returncode, done.stdout or "", done.stderr or "")
    except FileNotFoundError:
        ran = Ran(NOT_FOUND, "", f"{argv[0]} is not on PATH")
    except subprocess.TimeoutExpired:
        ran = Ran(TIMED_OUT, "", f"{argv[0]} timed out after {timeout or 0:.0f}s")
    except OSError as exc:
        ran = Ran(NOT_FOUND, "", str(exc))
    if check and not ran.ok:
        raise ProcError(argv, ran)
    return ran


def git(
    *args: str,
    cwd: Path | None = None,
    timeout: float | None = GIT_TIMEOUT,
    env: Mapping[str, str] | None = None,
    check: bool = False,
    stdin_text: str | None = None,
) -> Ran:
    """Run ``git <args>`` in *cwd*, hardened when *cwd* is under an untrusted folder. ``timeout=None`` waits."""
    if is_untrusted(cwd):
        return run(
            ["git", *HARDENING_ARGS, *args],
            cwd=cwd,
            timeout=timeout,
            env={**(env or {}), **HARDENING_ENV},
            check=check,
            stdin_text=stdin_text,
            scope="rig.proc.git",
        )
    return run(
        ["git", *args], cwd=cwd, timeout=timeout, env=env, check=check, stdin_text=stdin_text, scope="rig.proc.git"
    )


def git_out(*args: str, cwd: Path | None = None, timeout: float = GIT_TIMEOUT) -> str:
    """What ``git <args>`` prints, stripped; empty when it fails."""
    ran = git(*args, cwd=cwd, timeout=timeout)
    return ran.out.strip() if ran.ok else ""


def git_lines(*args: str, cwd: Path | None = None, timeout: float = GIT_TIMEOUT) -> list[str] | None:
    """The non-empty, stripped lines ``git <args>`` prints, or None when it fails."""
    ran = git(*args, cwd=cwd, timeout=timeout)
    if not ran.ok:
        return None
    return [line.strip() for line in ran.out.splitlines() if line.strip()]


def gh(
    *args: str,
    cwd: Path | None = None,
    timeout: float = GH_TIMEOUT,
    check: bool = False,
    stdin_text: str | None = None,
) -> Ran:
    """Run ``gh <args>``. *stdin_text* feeds a ``--input -`` request body."""
    return run(["gh", *args], cwd=cwd, timeout=timeout, check=check, stdin_text=stdin_text, scope="rig.proc.gh")


def gh_json[T](kind: type[T], *args: str, cwd: Path | None = None, timeout: float = GH_TIMEOUT) -> T | None:
    """Run ``gh <args>`` and decode its JSON stdout as *kind*. None when it fails or does not fit."""
    ran = gh(*args, cwd=cwd, timeout=timeout)
    if not ran.ok or not ran.out.strip():
        return None
    return try_decode_json(kind, ran.out)


# -- GraphQL seam ---------------------------------------------------------------


@dataclass(frozen=True)
class GhResult:
    """One GraphQL call's outcome, in the shape the PR resolvers read."""

    returncode: int
    stdout: str
    stderr: str


class GhRunner(Protocol):
    """Runs one GraphQL query. Production passes `run_gh`; tests pass a fake that answers from a table."""

    def __call__(self, query: str, /) -> GhResult: ...


GRAPHQL_TIMEOUT = 30.0


def run_gh(query: str) -> GhResult:
    """Run one GraphQL query through ``gh api graphql``."""
    ran = gh("api", "graphql", "-f", f"query={query}", timeout=GRAPHQL_TIMEOUT)
    return GhResult(ran.code, ran.out, ran.err)


# -- REST seam ------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class ApiRequest:
    """One GitHub REST call: ``GET repos/o/r/labels``, or a write whose JSON *body* is already encoded."""

    method: str
    route: str
    body: str | None = None


class GitHubApi(Protocol):
    """Runs one REST call. Production passes `gh_api`; tests pass a fake that answers by method and route.

    A failed call answers as ``gh api`` does: a non-zero code and ``HTTP <status>`` in ``err``.
    """

    def __call__(self, request: ApiRequest, /) -> Ran: ...


def gh_api(request: ApiRequest) -> Ran:
    """Run one REST call through ``gh api``; a body goes on stdin as ``--input -``."""
    args = ["api", request.route] if request.method == "GET" else ["api", "-X", request.method, request.route]
    if request.body is None:
        return gh(*args)
    return gh(*args, "--input", "-", stdin_text=request.body)
