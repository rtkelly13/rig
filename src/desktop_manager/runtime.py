"""Pinned copies of this repo for the background tasks to run from.

The scheduled tasks (Chrome Tabs Organise, Workspace Restore) must not run whatever
the checkout happens to hold: a pull, a branch switch or a half-finished edit
would change them mid-day. Instead each task runs from a copy of one commit:

    %LOCALAPPDATA%\\shared-utilities\\runtime\\
        <sha12>\\          the commit's files (`git archive`) plus its own `.venv`
            rig\\          `rig` and `rigw` published from the commit (`dotnet publish`)
            installed.json
        current            directory junction to the copy in use
        state.json         current and previous copy, and the repo they came from

A copy is a plain directory, not a worktree, so it carries no branch and git
commands never see it. It keeps the repo's layout, so code that finds files
relative to itself (the workspace plan, project templates) works unchanged, and
its environment comes from the commit's `uv.lock`. A new copy only becomes
`current` once its commands start and its workspace plan validates; the previous
one stays on disk for `rollback`, anything older is removed.
"""

from __future__ import annotations

import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

from pydantic import StrictStr

from rig.roots import app_data_dir
from rig.typed_json import decode_json

SHORT_SHA = 12
LINK_NAME = "current"
STATE_NAME = "state.json"
INSTALLED_NAME = "installed.json"
PLAN_RELATIVE = Path("desktop-manager") / "workspace-plan.bundle.json"
PUBLISH_NAME = "rig"
LAUNCHER_PROJECT = Path("src") / "Rig.Launcher"
COMMAND_TIMEOUT_SECONDS = 300
SYNC_TIMEOUT_SECONDS = 1800
TIMED_OUT = 124


@dataclass(frozen=True, slots=True)
class Ran:
    """How a command ended: its exit code and its stdout and stderr, joined and stripped."""

    code: int
    output: str


Runner = Callable[[Sequence[str], Path | None], Ran]


class InstallError(Exception):
    """A step of an install or rollback failed; the message says which and why."""


def base_dir() -> Path:
    return app_data_dir()


def runtime_root() -> Path:
    return base_dir() / "runtime"


def current_link(root: Path | None = None) -> Path:
    return (root or runtime_root()) / LINK_NAME


def traces_dir() -> Path:
    """Where tools run from a copy write their traces: outside every copy, so they outlive it."""
    return base_dir() / "traces"


def workspace_logs_dir() -> Path:
    """Where Workspace Restore logs when it runs from a copy: outside every copy, so history survives an update."""
    return base_dir() / "workspace-restore" / "logs"


def running_copy(module_file: str, root: Path | None = None) -> bool:
    """Whether `module_file` belongs to an installed copy rather than a checkout."""
    module = os.path.normcase(os.path.realpath(module_file))
    base = os.path.normcase(os.path.realpath(root or runtime_root()))
    try:
        return os.path.commonpath([module, base]) == base
    except ValueError:
        return False


def scripts_dir(copy: Path) -> Path:
    return copy / ".venv" / ("Scripts" if sys.platform == "win32" else "bin")


def executable(copy: Path, name: str) -> Path:
    return scripts_dir(copy) / (f"{name}.exe" if sys.platform == "win32" else name)


def published(copy: Path, name: str) -> Path:
    """`rig` or `rigw` as the copy's publish wrote it; the scheduled tasks run `rigw` through `current`."""
    return copy / PUBLISH_NAME / (f"{name}.exe" if sys.platform == "win32" else name)


@dataclass(frozen=True, slots=True)
class State:
    current: StrictStr | None = None
    previous: StrictStr | None = None
    repo: StrictStr | None = None


def read_state(root: Path) -> State:
    path = root / STATE_NAME
    if not path.is_file():
        return State()
    return decode_json(State, path.read_text(encoding="utf-8"))


def write_state(root: Path, state: State) -> None:
    payload = {"current": state.current, "previous": state.previous, "repo": state.repo}
    temporary = root / f"{STATE_NAME}.tmp"
    temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    temporary.replace(root / STATE_NAME)


@dataclass(frozen=True, slots=True)
class Installed:
    """A copy's `installed.json`: what it was built from and when. Every field is None when unknown."""

    sha: StrictStr | None = None
    ref: StrictStr | None = None
    repo: StrictStr | None = None
    installed_at: StrictStr | None = None


def read_installed(copy: Path) -> Installed:
    path = copy / INSTALLED_NAME
    if not path.is_file():
        return Installed()
    return decode_json(Installed, path.read_text(encoding="utf-8"))


def run(command: Sequence[str], cwd: Path | None = None) -> Ran:
    """Run without a terminal prompt and within a time limit, so a stalled fetch or sync fails the install."""
    env = {key: value for key, value in os.environ.items() if key not in {"VIRTUAL_ENV", "UV_PROJECT_ENVIRONMENT"}}
    env["GIT_TERMINAL_PROMPT"] = "0"
    timeout = SYNC_TIMEOUT_SECONDS if command[0] in {"uv", "dotnet"} else COMMAND_TIMEOUT_SECONDS
    try:
        result = subprocess.run(
            list(command),
            cwd=cwd,
            env=env,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            check=False,
            timeout=timeout,
        )
    except subprocess.TimeoutExpired:
        return Ran(TIMED_OUT, f"timed out after {timeout}s")
    return Ran(result.returncode, (result.stdout + result.stderr).strip())


def _must(runner: Runner, command: Sequence[str], cwd: Path | None = None) -> str:
    ran = runner(command, cwd)
    if ran.code != 0:
        raise InstallError(f"{' '.join(command)} failed ({ran.code}): {ran.output}")
    return ran.output


def resolve_commit(repo: Path, ref: str, *, fetch: bool, runner: Runner = run) -> str:
    if fetch and ref.startswith("origin/"):
        _must(runner, ["git", "-C", str(repo), "fetch", "--quiet", "origin"])
    return _must(runner, ["git", "-C", str(repo), "rev-parse", "--verify", f"{ref}^{{commit}}"]).strip()


def extract(repo: Path, sha: str, destination: Path) -> None:
    """Write the commit's tracked files to `destination`, which must not exist yet."""
    try:
        archive = subprocess.run(
            ["git", "-C", str(repo), "archive", "--format=tar", sha],
            capture_output=True,
            check=False,
            timeout=COMMAND_TIMEOUT_SECONDS,
        )
    except subprocess.TimeoutExpired as error:
        raise InstallError(f"git archive {sha} timed out after {COMMAND_TIMEOUT_SECONDS}s") from error
    if archive.returncode != 0:
        raise InstallError(f"git archive {sha} failed: {archive.stderr.decode(errors='replace').strip()}")
    destination.mkdir(parents=True)
    with tarfile.open(fileobj=io.BytesIO(archive.stdout)) as tar:
        tar.extractall(destination, filter="data")


def build_environment(copy: Path, runner: Runner = run) -> None:
    """The copy's own `.venv`, from its `uv.lock`.

    Files are copied rather than hard-linked from uv's cache, so cleaning the cache
    or a lock held on it by another environment never touches a running copy.
    """
    _must(runner, ["uv", "sync", "--project", str(copy), "--frozen", "--no-dev", "--link-mode", "copy"], copy)


def publish(copy: Path, runner: Runner = run) -> None:
    """The copy's own `rig` and `rigw`, published into `<copy>/rig` (`rigw` brings `rig` with it)."""
    command = ["dotnet", "publish", str(copy / LAUNCHER_PROJECT), "-c", "Release", "-o", str(copy / PUBLISH_NAME)]
    _must(runner, [*command, "-m:2", "-v", "q"], copy)


def check(copy: Path, runner: Runner = run) -> list[str]:
    """Problems that should stop `copy` becoming current; empty when it is fit to run."""
    desktop_manager = str(executable(copy, "desktop-manager"))
    python = str(executable(copy, "python"))
    commands: list[list[str]] = [
        [desktop_manager, "--help"],
        [desktop_manager, "chrome-tabs", "--help"],
        [str(published(copy, "rig")), "jobs", "run", "--help"],
    ]
    if sys.platform == "win32":
        commands.append([python, "-c", "import desktop_manager.chrome.scheduled, desktop_manager.windows_workspace"])
    else:
        commands.append([python, "-c", "import rig.web_host.server"])
    plan = copy / PLAN_RELATIVE
    if plan.is_file():
        commands.append([desktop_manager, "workspace", "validate-plan", "--config", str(plan)])
    problems: list[str] = []
    for command in commands:
        ran = runner(command, copy)
        if ran.code != 0:
            problems.append(f"{' '.join(command[1:])} exited {ran.code}: {ran.output[-2000:]}")
    return problems


def point_current(root: Path, copy: Path) -> None:
    """Repoint the `current` link at `copy`.

    A directory junction on Windows (no admin rights needed), a symlink elsewhere.
    Processes already running from the old copy keep their files: only the link moves.
    If the new link cannot be made, the old one is put back.
    """
    link = current_link(root)
    old: Path | None = None
    if _is_link(link):
        old = _link_target(link)
        _remove_link(link)
    elif link.exists():
        raise InstallError(f"{link} exists and is not a link; move it aside")
    try:
        _make_link(link, copy)
    except InstallError:
        if old is not None:
            _make_link(link, old)
        raise


def _is_link(path: Path) -> bool:
    return path.is_symlink() or path.is_junction()


def _link_target(link: Path) -> Path:
    target = str(link.readlink())
    return Path(target.removeprefix("\\\\?\\"))


def _remove_link(link: Path) -> None:
    if sys.platform == "win32":
        link.rmdir()
    else:
        link.unlink()


def _make_link(link: Path, target: Path) -> None:
    if sys.platform != "win32":
        link.symlink_to(target, target_is_directory=True)
        return
    result = subprocess.run(
        ["cmd", "/c", "mklink", "/J", str(link), str(target)], capture_output=True, text=True, check=False
    )
    if result.returncode != 0:
        raise InstallError(f"mklink /J {link} failed: {(result.stderr or result.stdout).strip()}")


def prune(root: Path, keep: set[str]) -> list[str]:
    """Remove copies other than `keep`; a copy still in use is left for the next install.

    Links are never followed: only real copy directories are removed.
    """
    removed: list[str] = []
    for entry in sorted(root.iterdir()):
        if entry.name in keep or entry.name == LINK_NAME or _is_link(entry) or not entry.is_dir():
            continue
        if not (entry / INSTALLED_NAME).is_file() and not entry.name.endswith(".partial"):
            continue
        shutil.rmtree(entry, ignore_errors=True)
        if not entry.exists():
            removed.append(entry.name)
    return removed


@dataclass(frozen=True, slots=True)
class InstallResult:
    sha: str
    copy: Path
    changed: bool
    previous: str | None
    removed: list[str]


def install(
    repo: Path,
    ref: str,
    *,
    root: Path | None = None,
    fetch: bool = True,
    runner: Runner = run,
    progress: Callable[[str], None] = lambda _message: None,
    now: Callable[[], datetime] = lambda: datetime.now(UTC),
) -> InstallResult:
    root = root or runtime_root()
    root.mkdir(parents=True, exist_ok=True)
    repo = Path(os.path.realpath(repo))
    state = read_state(root)
    sha = resolve_commit(repo, ref, fetch=fetch, runner=runner)
    name = sha[:SHORT_SHA]
    copy = root / name
    if state.current == name and current_link(root).exists():
        return InstallResult(sha, copy, changed=False, previous=state.previous, removed=[])

    if not (copy / INSTALLED_NAME).is_file():
        staging = root / f"{name}.partial"
        shutil.rmtree(staging, ignore_errors=True)
        shutil.rmtree(copy, ignore_errors=True)
        progress(f"Extracting {name} from {repo}")
        extract(repo, sha, staging)
        staging.rename(copy)
        progress("Building its environment from uv.lock and publishing rig")
        try:
            build_environment(copy, runner)
            publish(copy, runner)
        except InstallError:
            shutil.rmtree(copy, ignore_errors=True)
            raise

    progress("Checking it runs")
    problems = check(copy, runner)
    if problems:
        if not (copy / INSTALLED_NAME).is_file():
            shutil.rmtree(copy, ignore_errors=True)
        raise InstallError("the new copy failed its checks; current is unchanged:\n- " + "\n- ".join(problems))

    record = {"sha": sha, "ref": ref, "repo": str(repo), "installed_at": now().isoformat(timespec="seconds")}
    (copy / INSTALLED_NAME).write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    point_current(root, copy)
    previous = state.current if state.current != name else state.previous
    write_state(root, State(current=name, previous=previous, repo=str(repo)))
    removed = prune(root, {name, previous} if previous else {name})
    return InstallResult(sha, copy, changed=True, previous=previous, removed=removed)


def rollback(root: Path | None = None) -> State:
    root = root or runtime_root()
    state = read_state(root)
    if not state.previous or not (root / state.previous / INSTALLED_NAME).is_file():
        raise InstallError("no previous copy to roll back to")
    point_current(root, root / state.previous)
    swapped = State(current=state.previous, previous=state.current, repo=state.repo)
    write_state(root, swapped)
    return swapped


def commits_behind(repo: Path, sha: str, ref: str, runner: Runner = run) -> int | None:
    ran = runner(["git", "-C", str(repo), "rev-list", "--count", f"{sha}..{ref}"], None)
    return int(ran.output) if ran.code == 0 and ran.output.isdigit() else None
