"""The jobs coordinator: which jobs a run takes, what it records, and the tasks that drive it."""

from __future__ import annotations

import os
import xml.etree.ElementTree as ET
from dataclasses import replace
from datetime import datetime, timedelta
from pathlib import Path

import pytest

from desktop_manager.claude_sessions.startup_run import EXIT_DEFERRED
from desktop_manager.jobs import install
from desktop_manager.jobs.registry import (
    CLAUDE_SESSIONS_DEFERRED,
    RESTORE_DEFERRED,
    Cadence,
    Environment,
    Job,
    Trigger,
    default_jobs,
)
from desktop_manager.jobs.runner import log_path, plan, run, run_output
from desktop_manager.jobs.scheduled import prune_traces
from desktop_manager.jobs.state import (
    STATE_NAME,
    Decision,
    Outcome,
    RunRecord,
    last_run_path,
    read_run,
    read_state,
    write_state,
)
from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT

pytestmark = pytest.mark.ci

NOW = datetime(2026, 10, 10, 9, 0).astimezone()
BOOTED = NOW - timedelta(hours=1)
_NS = {"t": "http://schemas.microsoft.com/windows/2004/02/mit/task"}


def _job(
    name: str,
    cadence: Cadence = Cadence.EVERY_RUN,
    *,
    triggers=(Trigger.TICK,),
    requires=(),
    idle_minutes: int = 0,
    retry_exit_codes=(),
    detail_log: Path | None = None,
) -> Job:
    return Job(
        name=name,
        description=name,
        argv=("tool", name),
        triggers=frozenset(triggers),
        cadence=cadence,
        timeout_minutes=1,
        requires=tuple(requires),
        idle_minutes=idle_minutes,
        retry_exit_codes=frozenset(retry_exit_codes),
        detail_log=detail_log,
    )


def _printing(outputs: dict[str, tuple[int, str]]):
    """An executor that writes each job's output under a run-id header, as the real one does."""

    def execute(job: Job, logs: Path, run_id: str) -> int:
        code, text = outputs.get(job.name, (0, ""))
        path = log_path(logs, job)
        path.parent.mkdir(parents=True, exist_ok=True)
        with path.open("a", encoding="utf-8") as log:
            log.write(f"--- 2026-10-10 09:00:00 [{run_id}] tool {job.name}\n{text}--- exit {code}\n")
        return code

    return execute


def _ran(hours_ago: float, returncode: int = 0) -> Outcome:
    started = (NOW - timedelta(hours=hours_ago)).isoformat(timespec="seconds")
    return Outcome(started=started, seconds=1.0, returncode=returncode, trigger="tick")


def _decisions(jobs, trigger=Trigger.TICK, state=None, **kwargs) -> dict[str, str]:
    planned = plan(jobs, trigger, state or {}, now=NOW, booted=BOOTED, **kwargs)
    return {item.job.name: ("run" if item.run else item.reason) for item in planned}


def test_each_cadence_runs_only_when_due(tmp_path: Path):
    jobs = [
        _job("every"),
        _job("daily-fresh", Cadence.DAILY),
        _job("daily-stale", Cadence.DAILY),
        _job("boot-this", Cadence.ONCE_PER_BOOT),
        _job("boot-last", Cadence.ONCE_PER_BOOT),
        _job("logon-only", triggers=(Trigger.LOGON,)),
        _job("absent", requires=(tmp_path / "missing",)),
    ]
    state = {
        "every": _ran(0.1),
        "daily-fresh": _ran(19),
        "daily-stale": _ran(21),
        "boot-this": _ran(0.5),
        "boot-last": _ran(2),
    }

    assert _decisions(jobs, state=state) == {
        "every": "run",
        "daily-fresh": "not due (daily)",
        "daily-stale": "run",
        "boot-this": "not due (once-per-boot)",
        "boot-last": "run",
        "logon-only": "not on tick",
        "absent": f"missing {tmp_path / 'missing'}",
    }


def test_a_tick_retries_a_logon_job_that_could_not_run_yet_this_boot():
    jobs = [
        _job(name, triggers=(Trigger.LOGON,), retry_exit_codes=(4,))
        for name in ("deferred", "deferred-last-boot", "failed", "done", "never")
    ]
    state = {"deferred": _ran(0.5, 4), "deferred-last-boot": _ran(2, 4), "failed": _ran(0.5, 1), "done": _ran(0.5)}

    assert _decisions(jobs, state=state) == {
        "deferred": "run",
        "deferred-last-boot": "not on tick",
        "failed": "not on tick",
        "done": "not on tick",
        "never": "not on tick",
    }
    assert _decisions(jobs, state=state, trigger=Trigger.LOGON)["done"] == "run"


def test_claude_sessions_is_retried_when_startup_defers():
    assert CLAUDE_SESSIONS_DEFERRED == EXIT_DEFERRED
    env = Environment(
        scripts=Path("scripts"),
        plan=Path("plan.json"),
        workspace_root=Path("ws"),
        personal_root=Path("personal"),
        work_roots=(Path("acme"),),
        decisions_feed=Path("jobs") / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    (job,) = [job for job in default_jobs(env) if job.name == "claude-sessions"]
    assert job.retry_exit_codes == frozenset({EXIT_DEFERRED})


def test_restore_is_retried_when_screens_are_missing():
    env = Environment(
        scripts=Path("scripts"),
        plan=Path("plan.json"),
        workspace_root=Path("ws"),
        personal_root=Path("personal"),
        work_roots=(Path("acme"),),
        decisions_feed=Path("jobs") / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    (job,) = [job for job in default_jobs(env) if job.name == "restore"]
    assert job.retry_exit_codes == frozenset({RESTORE_DEFERRED})


def test_orca_jobs_target_the_personal_instance_and_wait_for_orca():
    env = Environment(
        scripts=Path("scripts"),
        plan=Path("plan.json"),
        workspace_root=Path("ws"),
        personal_root=Path("code") / "personal",
        work_roots=(Path("acme"),),
        decisions_feed=Path("jobs") / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    jobs = {job.name: job for job in default_jobs(env) if job.name in {"estate-orca", "orca-sync"}}
    assert set(jobs) == {"estate-orca", "orca-sync"}
    for job in jobs.values():
        assert "--apply" in job.argv
        assert job.argv[job.argv.index("--orca-data") + 1] == "orca-personal"
        assert job.cadence is Cadence.DAILY
        assert job.retry_exit_codes == frozenset({ORCA_UNAVAILABLE_EXIT})
    assert jobs["estate-orca"].argv[jobs["estate-orca"].argv.index("--code-root") + 1] == "code"
    assert jobs["orca-sync"].argv[jobs["orca-sync"].argv.index("--root") + 1] == str(Path("code") / "personal")


def test_estate_jobs_scan_the_whole_code_root():
    code = Path("code")
    env = Environment(
        scripts=Path("scripts"),
        plan=Path("plan.json"),
        workspace_root=Path("elsewhere") / "workspace",
        personal_root=code / "personal",
        work_roots=(code / "acme",),
        decisions_feed=Path("jobs") / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    jobs = {job.name: job for job in default_jobs(env)}
    for name in ("estate-sync", "estate-risk", "estate-trim"):
        argv = jobs[name].argv
        assert argv[argv.index("--root") + 1] == str(code), name
    decisions = jobs["estate-decisions"].argv
    roots = [decisions[i + 1] for i, arg in enumerate(decisions) if arg == "--root"]
    assert roots == [str(code), str(Path("elsewhere") / "workspace")]


def test_only_runs_the_named_job_whatever_its_trigger_or_cadence():
    jobs = [_job("a", Cadence.DAILY, triggers=(Trigger.LOGON,)), _job("b")]
    assert _decisions(jobs, state={"a": _ran(1)}, only=frozenset({"a"})) == {"a": "run"}


def test_a_run_records_every_outcome_and_fails_when_any_job_fails(tmp_path: Path):
    def execute(job: Job, _logs: Path, _run_id: str) -> int:
        if job.name == "boom":
            raise OSError("no such tool")
        return 3 if job.name == "bad" else 0

    jobs = [_job("good"), _job("bad"), _job("boom"), _job("later")]
    code = run(jobs, Trigger.TICK, tmp_path, booted=BOOTED, execute=execute, clock=lambda: NOW, report=lambda _: None)

    state = read_state(tmp_path / STATE_NAME)
    assert code == 1
    assert {name: (outcome.returncode, outcome.error, outcome.ok) for name, outcome in state.items()} == {
        "good": (0, None, True),
        "bad": (3, None, False),
        "boom": (None, "OSError: no such tool", False),
        "later": (0, None, True),
    }


def test_an_idle_gated_job_waits_for_an_idle_machine_unless_asked_for_by_name(tmp_path: Path):
    ran: list[str] = []

    def execute(job: Job, _logs: Path, _run_id: str) -> int:
        ran.append(job.name)
        return 0

    def tick(idle: float, only: frozenset[str] = frozenset()) -> None:
        run(
            [_job("always"), _job("quiet", idle_minutes=3)],
            Trigger.TICK,
            tmp_path,
            booted=BOOTED,
            only=only,
            execute=execute,
            idle_seconds=lambda: idle,
            clock=lambda: NOW,
            report=lambda _: None,
        )

    tick(idle=60)
    assert ran == ["always"]
    assert "quiet" not in read_state(tmp_path / STATE_NAME)

    tick(idle=180)
    tick(idle=0, only=frozenset({"quiet"}))
    assert ran == ["always", "always", "quiet", "quiet"]


def test_a_failed_job_keeps_the_end_of_its_output_and_the_run_is_recorded(tmp_path: Path):
    reasons = "".join(f"step {n}\n" for n in range(20)) + "!! worktree-reclaim: exit 1\n"
    execute = _printing({"fine": (0, "align: 0 moved, 11 in place\n"), "broken": (1, reasons)})
    lines: list[str] = []

    code = run(
        [_job("fine"), _job("broken"), _job("gated", idle_minutes=3)],
        Trigger.TICK,
        tmp_path,
        booted=BOOTED,
        execute=execute,
        idle_seconds=lambda: 0,
        clock=lambda: NOW,
        report=lines.append,
        run_id="tick-1",
    )

    fine = Outcome(NOW.isoformat(timespec="seconds"), 0.0, 0, "tick", None, "tick-1", "align: 0 moved, 11 in place")
    broken = Outcome(
        NOW.isoformat(timespec="seconds"),
        0.0,
        1,
        "tick",
        None,
        "tick-1",
        "!! worktree-reclaim: exit 1",
        (*(f"step {n}" for n in range(9, 20)), "!! worktree-reclaim: exit 1"),
    )
    record = read_run(last_run_path(tmp_path, Trigger.TICK))
    assert code == 1
    assert record == RunRecord(
        run_id="tick-1",
        trigger="tick",
        started=NOW.isoformat(timespec="seconds"),
        finished=NOW.isoformat(timespec="seconds"),
        decisions=(
            Decision("fine", ran=True, reason="every-run", outcome=fine),
            Decision("broken", ran=True, reason="every-run", outcome=broken),
            Decision("gated", ran=False, reason="in use (idle 0s, needs 3 min)"),
        ),
    )
    assert read_state(tmp_path / STATE_NAME) == {"fine": fine, "broken": broken}
    assert "       | !! worktree-reclaim: exit 1" in lines


def test_a_job_that_logs_elsewhere_is_explained_from_its_own_log(tmp_path: Path):
    own_log = tmp_path / "organise.log"
    own_log.write_text("--- 2026-10-10 08:45:00 organise\nClosed 1/1\n", encoding="utf-8")
    os.utime(own_log, (0, 0))
    printing = _printing({"chrome": (1, "")})

    def execute(job: Job, logs: Path, run_id: str) -> int:
        with own_log.open("a", encoding="utf-8") as log:
            log.write("--- 2026-10-10 09:00:00 organise\nNot closed #236: tab is still present\nClosed 0/1\n")
        return printing(job, logs, run_id)

    run([_job("chrome", detail_log=own_log)], Trigger.TICK, tmp_path, booted=BOOTED, execute=execute, clock=lambda: NOW)

    outcome = read_state(tmp_path / STATE_NAME)["chrome"]
    assert (outcome.summary, outcome.detail) == ("Closed 0/1", ("Not closed #236: tab is still present", "Closed 0/1"))


def test_a_job_that_died_before_logging_is_not_explained_by_an_earlier_run(tmp_path: Path):
    own_log = tmp_path / "organise.log"
    own_log.write_text("--- 2026-10-10 08:45:00 organise\nClosed 0/1\n", encoding="utf-8")

    run(
        [_job("chrome", detail_log=own_log)],
        Trigger.TICK,
        tmp_path,
        booted=BOOTED,
        execute=_printing({"chrome": (1, "")}),
        clock=lambda: NOW,
        report=lambda _: None,
    )

    outcome = read_state(tmp_path / STATE_NAME)["chrome"]
    assert (outcome.summary, outcome.detail) == (None, ())


def test_the_run_is_recorded_before_its_first_job_finishes(tmp_path: Path):
    seen: list[RunRecord | None] = []
    printing = _printing({})

    def execute(job: Job, logs: Path, run_id: str) -> int:
        seen.append(read_run(last_run_path(tmp_path, Trigger.TICK)))
        return printing(job, logs, run_id)

    run([_job("first")], Trigger.TICK, tmp_path, booted=BOOTED, execute=execute, clock=lambda: NOW, run_id="tick-1")

    assert seen == [RunRecord("tick-1", "tick", NOW.isoformat(timespec="seconds"), None, ())]


def test_run_output_reads_only_the_named_runs_section(tmp_path: Path):
    path = tmp_path / "job.log"
    path.write_text(
        "--- 2026-10-10 08:00:00 [tick-1] tool\nfirst\n--- exit 0\n"
        "--- 2026-10-10 08:15:00 [tick-2] tool\n\nsecond\n--- exit 1\n",
        encoding="utf-8",
    )
    assert (run_output(path, "tick-1"), run_output(path, "tick-2"), run_output(path)) == (
        ["first"],
        ["second"],
        ["second"],
    )
    assert (run_output(path, "tick-3"), run_output(tmp_path / "missing.log")) == ([], [])


def test_logon_prunes_only_old_trace_files(tmp_path: Path):
    old, recent, other = tmp_path / "a" / "old.ndjson", tmp_path / "b" / "new.ndjson", tmp_path / "a" / "old.txt"
    for path in (old, recent, other):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("{}", encoding="utf-8")
    now = NOW.timestamp()
    for path in (old, other):
        os.utime(path, (now - 15 * 86400, now - 15 * 86400))

    assert prune_traces(tmp_path, keep_days=14, now=now) == 1
    assert (old.exists(), recent.exists(), other.exists()) == (False, True, True)
    assert prune_traces(tmp_path / "absent", keep_days=14, now=now) == 0


def test_logon_restores_with_deferred_commands_then_resumes_sessions(tmp_path: Path):
    env = Environment(
        scripts=tmp_path / "Scripts",
        plan=tmp_path / "plan.json",
        workspace_root=tmp_path / "ws",
        personal_root=tmp_path / "personal",
        work_roots=(tmp_path / "acme",),
        decisions_feed=tmp_path / "jobs" / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    env.plan.write_text("{}", encoding="utf-8")
    jobs = {job.name: job for job in default_jobs(env)}

    on = _decisions(jobs.values(), Trigger.LOGON, flags={"claude-session-restore": True})
    assert [name for name, decision in on.items() if decision == "run"][:2] == ["restore", "claude-sessions"]
    assert "--defer-logon-commands" in jobs["restore"].argv
    assert (jobs["align"].triggers, jobs["align"].idle_minutes > 0) == (frozenset({Trigger.TICK}), True)


def test_state_survives_a_round_trip_and_reads_garbage_as_empty(tmp_path: Path):
    path = tmp_path / STATE_NAME
    write_state(path, {"a": _ran(1, returncode=2)})
    assert read_state(path) == {"a": _ran(1, returncode=2)}

    path.write_text('{"a": "not an outcome"}', encoding="utf-8")
    assert read_state(path) == {}
    path.write_text("not json", encoding="utf-8")
    assert read_state(path) == {}


def test_workspace_maintain_is_optional_and_restore_needs_the_plan(tmp_path: Path):
    env = Environment(
        scripts=tmp_path / "Scripts",
        plan=tmp_path / "plan.json",
        workspace_root=tmp_path / "ws",
        personal_root=tmp_path / "personal",
        work_roots=(tmp_path / "acme",),
        decisions_feed=tmp_path / "jobs" / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    decisions = _decisions(default_jobs(env), Trigger.LOGON)
    assert decisions["workspace-maintain"].startswith("missing ")
    assert decisions["restore"] == f"missing {tmp_path / 'plan.json'}"

    env.plan.write_text("{}", encoding="utf-8")
    assert _decisions(default_jobs(env), Trigger.LOGON)["restore"] == "run"


def test_without_a_workspace_repo_its_jobs_are_not_registered(tmp_path: Path):
    env = Environment(
        scripts=tmp_path / "Scripts",
        plan=tmp_path / "plan.json",
        workspace_root=None,
        personal_root=tmp_path / "personal",
        work_roots=(),
        decisions_feed=tmp_path / "jobs" / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    names = [job.name for job in default_jobs(env)]
    assert "worktree-reclaim" not in names
    assert "workspace-maintain" not in names
    assert "estate-decisions" in names
    assert env.estate_roots == (tmp_path,)


@pytest.mark.parametrize(
    ("spec", "trigger_path", "argument"),
    [
        (install.LOGON, "t:Triggers/t:LogonTrigger/t:Delay", "logon"),
        (install.TICK, "t:Triggers/t:TimeTrigger/t:Repetition/t:Interval", "tick"),
    ],
)
def test_tasks_run_the_coordinator_under_pythonw_as_the_interactive_user(spec, trigger_path, argument):
    xml = install.render_task_xml(
        spec,
        Path(r"C:\rt\current\.venv\Scripts\pythonw.exe"),
        user_id=r"CORP\me & you",
        start=datetime(2026, 10, 10, 9, 0),
    )
    root = ET.fromstring(xml.split("\n", 1)[1])

    def text(path: str) -> str | None:
        return root.findtext(path, namespaces=_NS)

    assert {
        "uri": text("t:RegistrationInfo/t:URI"),
        "trigger": text(trigger_path),
        "user": text("t:Principals/t:Principal/t:UserId"),
        "logon": text("t:Principals/t:Principal/t:LogonType"),
        "instances": text("t:Settings/t:MultipleInstancesPolicy"),
        "command": text("t:Actions/t:Exec/t:Command"),
        "arguments": text("t:Actions/t:Exec/t:Arguments"),
    } == {
        "uri": f"\\{spec.name}",
        "trigger": "PT5S" if spec is install.LOGON else "PT15M",
        "user": r"CORP\me & you",
        "logon": "InteractiveToken",
        "instances": "IgnoreNew",
        "command": r"C:\rt\current\.venv\Scripts\pythonw.exe",
        "arguments": f"-m desktop_manager.jobs.scheduled {argument}",
    }


def test_a_job_behind_a_rig_flag_runs_only_while_the_flag_is_on():
    jobs = [_job("gated", triggers=(Trigger.TICK,))]
    jobs = [replace(jobs[0], feature="claude-session-restore")]

    assert _decisions(jobs, flags={"claude-session-restore": False}) == {"gated": "flag claude-session-restore off"}
    assert _decisions(jobs, flags={"claude-session-restore": True}) == {"gated": "run"}
    assert _decisions(jobs, flags={}, only=frozenset({"gated"})) == {"gated": "run"}


def test_claude_sessions_follows_the_stored_flag_which_is_off_by_default():
    from rig.flags import set_override

    env = Environment(
        scripts=Path("scripts"),
        plan=Path(__file__),
        workspace_root=Path("ws"),
        personal_root=Path("personal"),
        work_roots=(Path("acme"),),
        decisions_feed=Path("jobs") / "decisions.json",
        orca_user_data=Path("orca-personal"),
    )
    (job,) = [job for job in default_jobs(env) if job.name == "claude-sessions"]

    assert _decisions([job], Trigger.LOGON) == {"claude-sessions": "flag claude-session-restore off"}
    set_override("claude-session-restore", True)
    assert _decisions([job], Trigger.LOGON) == {"claude-sessions": "run"}
