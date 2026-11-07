"""List agent tabs an Orca instance lost across a restart, with the commands to reopen them.

Read-only. Orca keeps its tabs in ``profiles/local-default/profile-state.db`` (domain
``workspaceSession``) and writes timestamped ``profile-state.db.backup.*.db`` copies beside it.
This compares the newest backup saved before the running Orca started with the live state
(read through SQLite's backup API, so a checkpoint mid-read cannot drop tabs) and reports
every tab that is gone, with the agent session it was running.

    uv run python -I orca_lost_tabs.py                      # this terminal's Orca instance
    uv run python -I orca_lost_tabs.py --orca-data "%APPDATA%/orca" --json
    uv run python -I orca_lost_tabs.py --before <backup.db>  # pick the snapshot yourself

Each lost agent tab is printed with an ``orca terminal create`` line that reopens it in the
worktree the tab sat in, ``cd``-ing first when the agent session ran somewhere else.
"""

from __future__ import annotations

import argparse
import json
import os
import platform
import shlex
import sqlite3
import sys
from contextlib import closing
from dataclasses import asdict, dataclass
from pathlib import Path

RESUME = {
    "claude": "claude --resume {id}",
    "opencode": "opencode --auto -s {id}",
    "agy": "agy --conversation {id} --dangerously-skip-permissions",
}


@dataclass
class LostTab:
    title: str
    tab_worktree: str
    agent: str | None
    session_id: str | None
    session_cwd: str | None
    transcript_exists: bool | None
    command: str | None


def default_orca_data() -> Path:
    env = os.environ.get("ORCA_USER_DATA_PATH")
    if env:
        return Path(env)
    system = platform.system()
    if system == "Windows":
        return Path(os.environ["APPDATA"]) / "orca"
    if system == "Darwin":
        return Path.home() / "Library/Application Support/orca"
    return Path.home() / ".config/orca"


def read_session(db: Path) -> tuple[dict[str, object], int]:
    with (
        closing(sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)) as src,
        closing(sqlite3.connect(":memory:")) as conn,
    ):
        src.backup(conn)  # one consistent snapshot, even while Orca is writing
        row = conn.execute(
            "select payload, updated_at from profile_state_documents where domain = 'workspaceSession'"
        ).fetchone()
    if row is None:
        raise SystemExit(f"{db}: no workspaceSession document")
    return json.loads(row[0]), int(row[1])


def pick_before(profile: Path, started_ms: int | None) -> Path:
    backups: list[tuple[int, Path]] = []
    for path in profile.glob("profile-state.db.backup.*.db"):
        _, updated = read_session(path)
        if started_ms is None or updated < started_ms:
            backups.append((updated, path))
    if not backups:
        raise SystemExit(
            f"{profile}: no backup saved before this Orca started. Orca keeps only its five newest "
            "hourly backups, so it has rotated away; pass --before, or fall back to daemon.log."
        )
    return max(backups)[1]


def pwsh_quote(text: str) -> str:
    return "'" + text.replace("'", "''") + "'"


def same_dir(a: str, b: str) -> bool:
    return os.path.normcase(os.path.normpath(a)) == os.path.normcase(os.path.normpath(b))


def lost_tabs(before: dict[str, object], after: dict[str, object]) -> list[LostTab]:
    before_tabs: dict[str, list[dict[str, object]]] = before.get("unifiedTabs") or {}
    after_tabs: dict[str, list[dict[str, object]]] = after.get("unifiedTabs") or {}
    still_open = {str(t["id"]) for tabs in after_tabs.values() for t in tabs}
    reopened = {
        str(t["aiVaultTitle"]["sessionId"])
        for tabs in after_tabs.values()
        for t in tabs
        if isinstance(t.get("aiVaultTitle"), dict)
    }
    sleeping: dict[str, dict[str, object]] = before.get("sleepingAgentSessionsByPaneKey") or {}
    by_tab = {str(v.get("tabId")): v for v in sleeping.values()}

    result: list[LostTab] = []
    for worktree_id, tabs in before_tabs.items():
        tab_dir = worktree_id.split("::", 1)[-1]
        for tab in tabs:
            if str(tab["id"]) in still_open:
                continue
            vault: dict[str, object] = tab.get("aiVaultTitle") or {}
            sleep = by_tab.get(str(tab["id"]), {})
            provider: dict[str, object] = sleep.get("providerSession") or {}
            agent = str(vault.get("agent") or sleep.get("agent") or "") or None
            session_id = str(vault.get("sessionId") or provider.get("id") or "") or None
            if session_id and session_id in reopened:
                continue
            cwd = str(provider.get("cwd") or "").replace("\\", "/") or None
            transcript = str(provider.get("transcriptPath") or "")
            command = None
            if agent and session_id and agent in RESUME:
                resume = RESUME[agent].format(id=session_id)
                inner = resume if not cwd or same_dir(cwd, tab_dir) else f"cd {pwsh_quote(cwd)}; {resume}"
                command = (
                    f"orca terminal create --worktree {shlex.quote('path:' + tab_dir)} "
                    f"--title {shlex.quote(str(tab.get('label') or ''))} --command {shlex.quote(inner)} --json"
                )
            result.append(
                LostTab(
                    title=str(tab.get("label") or ""),
                    tab_worktree=tab_dir,
                    agent=agent,
                    session_id=session_id,
                    session_cwd=cwd,
                    transcript_exists=Path(transcript).exists() if transcript else None,
                    command=command,
                )
            )
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--orca-data", type=Path, default=None, help="Orca user-data folder (default: this terminal's)")
    parser.add_argument("--before", type=Path, default=None, help="backup to compare from (default: newest pre-start)")
    parser.add_argument("--json", action="store_true", help="emit JSON")
    args = parser.parse_args()

    data: Path = args.orca_data or default_orca_data()
    profile = data / "profiles" / "local-default"
    runtime = data / "orca-runtime.json"
    started_ms = json.loads(runtime.read_text(encoding="utf-8")).get("startedAt") if runtime.exists() else None
    before_db: Path = args.before or pick_before(profile, started_ms)

    before, before_ms = read_session(before_db)
    after = read_session(profile / "profile-state.db")[0]
    tabs = lost_tabs(before, after)

    sys.stdout.reconfigure(encoding="utf-8")
    if args.json:
        payload = {"orca_data": str(data), "before": str(before_db), "before_saved_ms": before_ms}
        sys.stdout.write(json.dumps({**payload, "lost": [asdict(t) for t in tabs]}, indent=2) + "\n")
        return 0
    sys.stdout.write(f"Orca data: {data}\nCompared from: {before_db.name}\n\n")
    if not tabs:
        sys.stdout.write("No tabs lost.\n")
    for tab in tabs:
        what = f"{tab.agent} {tab.session_id}" if tab.session_id else "plain shell"
        sys.stdout.write(f"- {tab.title}  [{tab.tab_worktree}]  {what}")
        if tab.transcript_exists is False:
            sys.stdout.write("  (transcript missing)")
        sys.stdout.write("\n")
        if tab.command:
            sys.stdout.write(f"    {tab.command}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
