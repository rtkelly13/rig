"""The jobs the coordinator runs, and when each one is due.

Every recurring thing that keeps the machine aligned is one entry here, instead
of a Task Scheduler task of its own. Two scheduled tasks drive them all (see
`install`): one at logon and one every `TICK_MINUTES`. Each run names its
trigger, and a job runs on the triggers it lists, subject to its cadence:

- `Cadence.EVERY_RUN` runs every time its trigger fires;
- `Cadence.ONCE_PER_BOOT` runs on the first of its triggers after a boot;
- `Cadence.DAILY` runs when its last start is `DAILY_HOURS` old.

A job whose `requires` paths are missing is skipped, not failed, so jobs backed
by another checkout (the workspace repo) are optional enrichment. A job with a
`feature` runs only while that rig flag is on (`rig flags list`).
"""

from __future__ import annotations

import enum
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT

TICK_MINUTES = 15
DAILY_HOURS = 20
ALIGN_IDLE_MINUTES = 3


class Trigger(enum.StrEnum):
    LOGON = "logon"
    TICK = "tick"


class Cadence(enum.StrEnum):
    EVERY_RUN = "every-run"
    ONCE_PER_BOOT = "once-per-boot"
    DAILY = "daily"


#: `claude-sessions startup` typed nothing: the workstation was locked or the plan's screens were missing.
CLAUDE_SESSIONS_DEFERRED = 4
#: `workspace restore` found fewer screens than the plan places windows on and opened nothing.
RESTORE_DEFERRED = 4


@dataclass(frozen=True, slots=True)
class Job:
    name: str
    description: str
    argv: tuple[str, ...]
    triggers: frozenset[Trigger]
    cadence: Cadence
    timeout_minutes: int
    requires: tuple[Path, ...] = ()
    #: Run only once nobody has touched the keyboard or mouse for this long; 0 runs regardless.
    idle_minutes: int = 0
    #: Exit codes meaning "could not run yet": a tick reruns the job until it exits otherwise, once per boot.
    retry_exit_codes: frozenset[int] = frozenset()
    #: The `--- `-headed log the job writes its own narrative to, when that is not stdout.
    detail_log: Path | None = None
    #: The rig flag that must be on for the job to run; naming the job in `--only` runs it anyway.
    feature: str | None = None


@dataclass(frozen=True, slots=True)
class Environment:
    """Where the jobs' commands live on this machine."""

    scripts: Path
    plan: Path
    #: The work workspace checkout (`tools.estate.workspace_repo`); None leaves its jobs out.
    workspace_root: Path | None
    #: The directory holding the personal clones; its parent is the code root every `estate` job scans.
    personal_root: Path
    #: This machine's work folders (`rig.scopes.work_dirs`); `estate decisions` skips any that are absent.
    work_roots: tuple[Path, ...]
    #: Where `estate decisions` writes its feed, beside the coordinator's own state.
    decisions_feed: Path
    #: The Orca instance that holds the personal estate (`orca-personal` on Windows).
    orca_user_data: Path

    @property
    def code_root(self) -> Path:
        """The folder holding `personal` and the org and work folders beside it."""
        return self.personal_root.parent

    @property
    def estate_roots(self) -> tuple[Path, ...]:
        """The code root, plus the work and workspace folders when they live outside it."""
        workspace = () if self.workspace_root is None else (self.workspace_root,)
        extra = [root for root in (*self.work_roots, *workspace) if not root.is_relative_to(self.code_root)]
        return (self.code_root, *extra)

    def _workspace(self) -> Path:
        if self.workspace_root is None:
            raise ValueError("No workspace repo is configured (tools.estate.workspace_repo)")
        return self.workspace_root

    @property
    def workspace_python(self) -> Path:
        return self._workspace() / ".venv" / "Scripts" / "python.exe"

    @property
    def workspace_maintain(self) -> Path:
        return self._workspace() / "src" / "workspace_cli" / "maintain" / "__main__.py"

    @property
    def workspace_cli(self) -> Path:
        return self._workspace() / "src" / "workspace_cli" / "main.py"

    def script(self, name: str) -> str:
        suffix = ".exe" if os.name == "nt" else ""
        return str(self.scripts / f"{name}{suffix}")


def default_jobs(env: Environment) -> tuple[Job, ...]:
    """The jobs, in the order a run takes them."""
    from desktop_manager.chrome.install_location import LOG_NAME, install_root

    desktop_manager = env.script("desktop-manager")
    python = str(env.scripts / ("python.exe" if os.name == "nt" else "python"))
    both = frozenset({Trigger.LOGON, Trigger.TICK})
    return (
        Job(
            name="restore",
            description="Open and place every window in the workspace plan.",
            argv=(desktop_manager, "workspace", "restore", "--config", str(env.plan), "--defer-logon-commands"),
            triggers=frozenset({Trigger.LOGON}),
            cadence=Cadence.EVERY_RUN,
            timeout_minutes=45,
            requires=(env.plan,),
            retry_exit_codes=frozenset({RESTORE_DEFERRED}),
        ),
        Job(
            name="claude-sessions",
            description="Resume Claude sessions the last shutdown cut off; start `claude` in empty terminals.",
            argv=(desktop_manager, "claude-sessions", "startup", "--config", str(env.plan)),
            triggers=frozenset({Trigger.LOGON}),
            cadence=Cadence.EVERY_RUN,
            timeout_minutes=20,
            requires=(env.plan,),
            retry_exit_codes=frozenset({CLAUDE_SESSIONS_DEFERRED}),
            feature="claude-session-restore",
        ),
        Job(
            name="chrome-organise",
            description="Close merged, duplicate and superseded Chrome tabs, and group the rest, once idle.",
            argv=(python, "-m", "desktop_manager.chrome.scheduled", "--wait-minutes", str(TICK_MINUTES - 5)),
            triggers=frozenset({Trigger.TICK}),
            cadence=Cadence.EVERY_RUN,
            timeout_minutes=TICK_MINUTES + 5,
            detail_log=install_root() / LOG_NAME,
        ),
        Job(
            name="align",
            description="Put open plan windows back on their desktop and slot, launching nothing.",
            argv=(desktop_manager, "workspace", "align", "--config", str(env.plan)),
            triggers=frozenset({Trigger.TICK}),
            cadence=Cadence.EVERY_RUN,
            timeout_minutes=5,
            requires=(env.plan,),
            idle_minutes=ALIGN_IDLE_MINUTES,
        ),
        Job(
            name="estate-sync",
            description="`git fetch --prune` across every clone in the estate, so later checks see gone branches.",
            argv=(env.script("estate"), "sync", "--root", str(env.code_root)),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=30,
        ),
        Job(
            name="estate-risk",
            description="Report clones across the estate holding uncommitted or unpushed work.",
            argv=(env.script("estate"), "risk", "--root", str(env.code_root)),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=30,
        ),
        Job(
            name="estate-trim",
            description="Delete node_modules, venvs and build output in estate worktrees idle 7+ days.",
            argv=(env.script("estate"), "trim", "--apply", "--root", str(env.code_root)),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=30,
        ),
        Job(
            name="estate-orca",
            description="Register every cloned managed project in the personal Orca, in its group.",
            argv=(
                env.script("estate"),
                "orca",
                "--apply",
                "--code-root",
                str(env.code_root),
                "--orca-data",
                str(env.orca_user_data),
            ),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=10,
            retry_exit_codes=frozenset({ORCA_UNAVAILABLE_EXIT}),
        ),
        Job(
            name="orca-sync",
            description="Apply config/orca.json to the personal Orca: agent default args, repo colours, scripts, refs.",
            argv=(
                env.script("workspace"),
                "orca",
                "sync",
                "--apply",
                "--root",
                str(env.personal_root),
                "--orca-data",
                str(env.orca_user_data),
            ),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=10,
            retry_exit_codes=frozenset({ORCA_UNAVAILABLE_EXIT}),
        ),
        Job(
            name="otel-collector",
            description="Keep the local OTel collector listening on 127.0.0.1:4317/4318; prune its files past 7 days.",
            argv=(env.script("otel-stack"), "collector", "ensure"),
            triggers=both,
            cadence=Cadence.EVERY_RUN,
            timeout_minutes=5,
        ),
        Job(
            name="estate-prune-caches",
            description="Prune the uv, pnpm, NuGet http and Docker build caches, and Claude temp idle 7+ days.",
            argv=(env.script("estate"), "prune-caches", "--apply"),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=60,
        ),
        *_workspace_jobs(env, "before-decisions"),
        Job(
            name="estate-decisions",
            description="Write the feed of worktrees and directories only a human can decide about.",
            argv=(
                env.script("estate"),
                "decisions",
                *(arg for root in env.estate_roots for arg in ("--root", str(root))),
                "--write",
                str(env.decisions_feed),
            ),
            triggers=both,
            cadence=Cadence.DAILY,
            timeout_minutes=30,
        ),
        *_workspace_jobs(env, "last"),
    )


def _workspace_jobs(env: Environment, place: Literal["before-decisions", "last"]) -> tuple[Job, ...]:
    """The jobs that run the work workspace repo's own CLI, or none when no workspace repo is configured."""
    if env.workspace_root is None:
        return ()
    both = frozenset({Trigger.LOGON, Trigger.TICK})
    if place == "before-decisions":
        return (
            Job(
                name="worktree-reclaim",
                description="Remove merged worktrees across every work repo (`workspace worktree reclaim`).",
                argv=(
                    str(env.workspace_python),
                    "-m",
                    "workspace_cli.main",
                    "worktree",
                    "reclaim",
                    "--all-repos",
                    "--apply",
                ),
                triggers=both,
                cadence=Cadence.DAILY,
                timeout_minutes=60,
                requires=(env.workspace_python, env.workspace_cli),
            ),
        )
    return (
        Job(
            name="workspace-maintain",
            description="The workspace repo's own due housekeeping (`workspace maintain run --due`).",
            argv=(str(env.workspace_python), "-m", "workspace_cli.maintain", "run", "--due"),
            triggers=both,
            cadence=Cadence.EVERY_RUN,
            timeout_minutes=60,
            requires=(env.workspace_python, env.workspace_maintain),
        ),
    )


def current_environment() -> Environment:
    """The environment this process runs in: its own scripts, and the plan its restore uses.

    Run from an installed runtime copy, the plan is the copy's bundled one, and
    restore logs and the jobs' traces go outside every copy, so they outlive it;
    from a checkout, the per-user plan.

    The personal root is passed to `estate` explicitly. Left to itself, `estate`
    places it beside the checkout its code sits in, which for an installed copy
    is the runtime directory, where it finds no clones at all. The copy's
    `state.json` records the checkout it came from, and that checkout's parent
    is the personal root."""
    import sys

    from desktop_manager import runtime
    from desktop_manager.plan import LOG_DIR_ENV, default_plan_path
    from rig import roots, scopes
    from rig.estate.decisions import default_feed_path
    from rig.estate.orca_runtime import estate_user_data
    from rig.tracing import TRACE_HOME_ENV

    personal_root = roots.personal_dir()
    if runtime.running_copy(__file__):
        plan = runtime.current_link() / runtime.PLAN_RELATIVE
        os.environ.setdefault(LOG_DIR_ENV, str(runtime.workspace_logs_dir()))
        os.environ.setdefault(TRACE_HOME_ENV, str(runtime.traces_dir()))
        source = runtime.read_state(runtime.runtime_root()).repo
        if source:
            personal_root = Path(source).parent
    else:
        plan = default_plan_path()
    return Environment(
        scripts=Path(sys.executable).parent,
        plan=plan,
        workspace_root=scopes.workspace_repo(personal_root.parent),
        personal_root=personal_root,
        work_roots=scopes.work_dirs(personal_root.parent),
        decisions_feed=default_feed_path(),
        orca_user_data=estate_user_data(),
    )
