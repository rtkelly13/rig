"""Triage ordering, failure isolation, history, Markdown and dashboard API contracts."""

from __future__ import annotations

import json
import subprocess
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator
from contextlib import closing
from dataclasses import dataclass, replace
from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.db.migrations import run_migrations
from rig.triage import agents, github, markdown, service, store
from rig.triage.cli import app
from rig.triage.models import Activity, Entry, Interaction, Item, rank
from rig.typed_json import decode_json
from rig.web_host.server import StudioServer

pytestmark = pytest.mark.ci


def item(number: int = 1, **kwargs: object) -> Item:
    base = Item(
        "owner/repo",
        number,
        "pr",
        "Change | <script> & links",
        "OPEN",
        "2026-01-01T00:00:00Z",
        "2026-01-02T00:00:00Z",
        "a" * 40,
        4,
        0,
        10,
    )
    # Tests use dataclass replacement with named fields; no anonymous production records.
    return replace(base, **kwargs)


def test_ordering_and_stable_tiebreaks() -> None:
    candidates = [
        item(9),
        item(8, lines=1),
        item(7, priority=0, readiness=3),
        item(6, created="2025-01-01T00:00:00Z"),
        item(5),
        item(4, readiness=1),
    ]
    assert [i.number for i in sorted(candidates, key=rank)] == [7, 8, 6, 5, 9, 4]
    assert markdown.render(candidates) == markdown.render(list(reversed(candidates)))


def test_markdown_roundtrip_preserves_prose_and_is_noop(tmp_path: Path) -> None:
    path = tmp_path / "Queue.md"
    rows = [Entry(item(), ())]
    markdown.export(path, rows)
    path.write_text("My process\n\n" + path.read_text(encoding="utf-8") + "\nMy notes\n", encoding="utf-8")
    markdown.export(path, rows)
    before, stat = path.read_bytes(), path.stat()
    runner = CliRunner()
    assert runner.invoke(app, ["verify", str(path)]).exit_code == 0
    markdown.export(path, rows)
    assert path.read_bytes() == before
    assert path.stat().st_mtime_ns == stat.st_mtime_ns
    assert b"My notes" in before and b"&#124;" in before and b"&lt;script&gt;" in before


@pytest.mark.parametrize(
    "damage",
    [
        lambda text: text.replace("| 1 |", "| 7 |"),
        lambda text: text.replace("P0", "P9"),
        lambda text: text.replace("/pull/1", "/pull/9"),
        lambda text: text.replace(markdown.END, ""),
        lambda text: text.replace("a" * 40, "bad-head"),
        lambda text: text.replace("| 1 |", "| 1 | extra |"),
        lambda text: text + "\n" + text,
    ],
)
def test_verify_rejects_damage_without_repair(tmp_path: Path, damage) -> None:
    path = tmp_path / "Queue.md"
    broken = damage(markdown.render([item(priority=0)]))
    path.write_text(broken, encoding="utf-8")
    result = CliRunner().invoke(app, ["verify", str(path)])
    assert result.exit_code == 1
    assert path.read_text(encoding="utf-8") == broken


def test_history_survives_updates_deferrals_and_merge(tmp_path: Path) -> None:
    with closing(store.connect(tmp_path / "rig.db")) as conn:
        assert run_migrations(conn) == 0
        with conn:
            store.save(conn, [item()])
            store.interact(conn, Interaction("owner/repo", 1, "reviewed", "a" * 40, "Looks good"))
            store.interact(conn, Interaction("owner/repo", 1, "deferred", "a" * 40, "Later"))
            store.save(conn, [item(head="b" * 40)])
        assert store.queue(conn) == []
        with pytest.raises(ValueError, match="head changed"):
            store.interact(conn, Interaction("owner/repo", 1, "reviewed", "a" * 40))
        with conn:
            store.interact(conn, Interaction("owner/repo", 1, "resumed", "b" * 40))
        assert len(store.queue(conn)) == 1
        activity = [Activity("review/1", "2026-01-02T00:00:00Z", "me", "github:approved", "a" * 40, "LGTM")]
        with conn:
            store.import_activity(conn, item(), activity)
            store.import_activity(conn, item(), activity)
            store.save(conn, [item(head="b" * 40, state="MERGED")])
            store.save(conn, [item(head="b" * 40, state="MERGED")])
        saved = store.entries(conn)[0]
        assert store.queue(conn) == []
        assert len(saved.events) == 7
        assert saved.events[1].note == "Looks good"
        assert len([event for event in saved.events if event.actor == "me"]) == 1


def test_refresh_failure_retains_prior_snapshots_and_markdown(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    db, output = tmp_path / "rig.db", tmp_path / "Queue.md"
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, [item()])
        markdown.export(output, store.entries(conn))
    before = output.read_bytes()
    monkeypatch.setattr(github, "repositories", lambda _config: ["owner/repo", "owner/failure"])

    def fetch(repo: str, kind: str) -> list[Item]:
        if repo.endswith("failure"):
            raise subprocess.CalledProcessError(1, ["gh"])
        return [item(title="new title")] if kind == "pr" else []

    monkeypatch.setattr(github, "fetch", fetch)
    with pytest.raises(subprocess.CalledProcessError):
        service.refresh(db, output)
    assert store.read_entries(db)[0].item.title == item().title
    assert output.read_bytes() == before


def test_missing_open_item_is_observed_as_merged(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    db, output = tmp_path / "rig.db", tmp_path / "Queue.md"
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, [item()])
    monkeypatch.setattr(github, "repositories", lambda _config: ["owner/repo"])
    monkeypatch.setattr(github, "fetch", lambda _repo, _kind: [])
    monkeypatch.setattr(github, "observe", lambda old: replace(old, state="MERGED"))
    result = service.refresh(db, output)
    assert result[0].item.state == "MERGED"
    assert "owner/repo#1" not in output.read_text(encoding="utf-8")
    assert len(result[0].events) == 2


def test_paginated_github_and_registry(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    registry = tmp_path / "managed.json"
    registry.write_text(
        json.dumps(
            {
                "projects": [
                    {"repo": "git@github.com:Owner/Repo.git"},
                    {"repo": "git@github.com:Owner/Archived.git", "archived": True},
                    {"repo": "git@github.com:Owner/Disabled.git", "enabled": False},
                ]
            }
        ),
        encoding="utf-8",
    )
    assert github.repositories(registry) == ["owner/repo"]
    calls: list[list[str]] = []

    def run(args: list[str]) -> str:
        calls.append(args)
        return json.dumps(
            [
                {
                    "data": {
                        "repository": {
                            "items": {
                                "nodes": [
                                    {
                                        "number": n,
                                        "title": "PR",
                                        "body": "## Reason\nKeep the workflow",
                                        "state": "OPEN",
                                        "createdAt": "2026-01-01T00:00:00Z",
                                        "updatedAt": "2026-01-02T00:00:00Z",
                                        "labels": {"nodes": [{"name": "priority: P1"}]},
                                        "headRefOid": "a" * 40,
                                        "mergeStateStatus": "CLEAN",
                                    }
                                ]
                            }
                        }
                    }
                }
                for n in (1, 2)
            ]
        )

    monkeypatch.setattr(github, "run", run)
    result = github.fetch("owner/repo", "pr")
    assert [pr.number for pr in result] == [1, 2]
    assert result[0].body == "## Reason\nKeep the workflow"
    assert result[0].priority == 1 and result[0].readiness == 0
    assert "--paginate" in calls[0] and "--slurp" in calls[0]


@dataclass(frozen=True)
class Running:
    url: str
    db: Path


@pytest.fixture
def running(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[Running]:
    monkeypatch.setattr(service, "queue_path", lambda: tmp_path / "queue.md")
    monkeypatch.setattr(agents, "signals", dict)
    db = tmp_path / "rig.db"
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, [item()])
    server = StudioServer(port=0, db_path=db)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield Running(f"http://127.0.0.1:{server.server_address[1]}", db)
    finally:
        server.shutdown()
        server.server_close()
        thread.join()


def test_dashboard_uses_its_database_and_records_actions(running: Running) -> None:
    with urllib.request.urlopen(running.url + "/api/triage") as response:
        assert len(json.load(response)["entries"]) == 1
    request = urllib.request.Request(
        running.url + "/api/triage/interact",
        data=json.dumps(
            {"repo": "owner/repo", "number": 1, "action": "note", "head": "a" * 40, "note": "Follow up"}
        ).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request) as response:
        assert json.load(response)["entries"][0]["events"][-1]["note"] == "Follow up"
    assert store.read_entries(running.db)[0].events[-1].note == "Follow up"
    request.add_header("Origin", "https://untrusted.example")
    with pytest.raises(urllib.error.HTTPError) as error:
        urllib.request.urlopen(request)
    assert error.value.code == 400
    assert len(store.read_entries(running.db)[0].events) == 2


def test_review_decision_rechecks_live_head(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    db, output = tmp_path / "rig.db", tmp_path / "Queue.md"
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, [item()])
    monkeypatch.setattr(github, "observe", lambda old: replace(old, head="b" * 40))
    with pytest.raises(ValueError, match="head changed"):
        service.record(Interaction("owner/repo", 1, "reviewed", "a" * 40), db, output)
    saved = store.read_entries(db)[0]
    assert saved.item.head == "b" * 40
    assert all(event.action != "reviewed" for event in saved.events)
    assert "b" * 40 in output.read_text(encoding="utf-8")


def test_removed_repository_leaves_queue_without_losing_history(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    db, output = tmp_path / "rig.db", tmp_path / "Queue.md"
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, [item()])
        store.interact(conn, Interaction("owner/repo", 1, "note", "a" * 40, "Keep this context"))
    monkeypatch.setattr(github, "repositories", lambda _config: ["owner/other"])
    monkeypatch.setattr(github, "fetch", lambda _repo, _kind: [])
    service.refresh(db, output)
    with closing(store.connect(db)) as conn:
        assert store.queue(conn) == []
        assert store.entries(conn)[0].events[1].note == "Keep this context"
    assert "owner/repo#1" not in output.read_text(encoding="utf-8")


def test_markdown_detects_reordered_rows_and_duplicate_items() -> None:
    text = markdown.render([item(1), item(2)])
    rows = text.splitlines()
    rows[3], rows[4] = rows[4], rows[3]
    with pytest.raises(ValueError, match="order"):
        markdown.verify("\n".join(rows))
    with pytest.raises(ValueError, match="Duplicate"):
        markdown.verify(markdown.render([item(1), item(1)]))


def test_github_activity_imports_inline_reviews_and_comments(monkeypatch: pytest.MonkeyPatch) -> None:
    calls: list[list[str]] = []

    def run(args: list[str]) -> str:
        calls.append(args)
        return json.dumps(
            [
                [
                    {
                        "id": 11,
                        "user": {"login": "reviewer"},
                        "body": "Consider this",
                        "updated_at": "2026-01-02T00:00:00Z",
                        "commit_id": "a" * 40,
                    }
                ]
            ]
        )

    monkeypatch.setattr(github, "run", run)
    events = github.activity(item())
    assert len(events) == 3
    assert len({event.key for event in events}) == 3
    assert all(event.actor == "reviewer" for event in events)
    assert any("/pulls/1/comments" in args[1] for args in calls)


def test_refresh_keeps_transient_queue_outside_vault(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    vault = tmp_path / "Vault"
    root = tmp_path / "rig"
    monkeypatch.setenv("VAULT_PATH", str(vault))
    monkeypatch.setattr(service, "config_file_path", lambda: root / "config" / "managed-projects.json")
    monkeypatch.setattr(github, "repositories", lambda _config: ["owner/repo"])
    monkeypatch.setattr(github, "fetch", lambda _repo, kind: [item()] if kind == "pr" else [])
    service.refresh(root / "temp" / "rig.db")
    queue = root / "temp" / "triage" / "queue.md"
    markdown.verify(queue.read_text(encoding="utf-8"))
    assert not vault.exists()
    assert "[[" not in queue.read_text(encoding="utf-8")


def test_archive_returns_on_new_head_or_human_activity(tmp_path: Path) -> None:
    with closing(store.connect(tmp_path / "rig.db")) as conn:
        with conn:
            store.save(conn, [item()])
            store.interact(conn, Interaction("owner/repo", 1, "archived", "a" * 40))
        assert store.entries(conn)[0].archived
        assert store.queue(conn) == []
        with conn:
            store.save(conn, [item(head="b" * 40)])
        assert not store.entries(conn)[0].archived
        with conn:
            store.interact(conn, Interaction("owner/repo", 1, "archived", "b" * 40))
            store.import_activity(
                conn, item(), [Activity("bot/1", "2099-01-01T00:00:00Z", "ci[bot]", "github:commented", "", "CI")]
            )
        assert store.entries(conn)[0].archived
        with conn:
            store.import_activity(
                conn,
                item(),
                [Activity("human/1", "2099-01-01T00:00:00Z", "reviewer", "github:commented", "", "Please revisit")],
            )
        assert not store.entries(conn)[0].archived
        assert len(store.queue(conn)) == 1


def test_description_survives_observation_and_old_snapshots(monkeypatch: pytest.MonkeyPatch) -> None:
    raw = json.dumps(
        {
            "number": 1,
            "title": "PR",
            "state": "OPEN",
            "createdAt": "2026-01-01T00:00:00Z",
            "updatedAt": "2026-01-02T00:00:00Z",
            "labels": {"nodes": []},
            "body": "Fresh description",
            "headRefOid": "a" * 40,
        }
    )
    raw = json.dumps({"data": {"repository": {"item": json.loads(raw)}}})
    monkeypatch.setattr(github, "run", lambda _args: raw)
    assert github.observe(item()).body == "Fresh description"
    assert (
        decode_json(
            Item,
            json.dumps(
                {
                    "repo": "owner/repo",
                    "number": 1,
                    "kind": "pr",
                    "title": "Old snapshot",
                    "state": "OPEN",
                    "created": "2026-01-01T00:00:00Z",
                    "updated": "2026-01-01T00:00:00Z",
                }
            ),
        ).body
        is None
    )


@pytest.mark.parametrize("action", ["archived", "reviewed", "changes_requested"])
def test_refresh_imports_replies_for_retained_items(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, action: str
) -> None:
    db, output = tmp_path / "rig.db", tmp_path / "Queue.md"
    with closing(store.connect(db)) as conn, conn:
        store.save(conn, [item()])
        store.interact(
            conn,
            decode_json(
                Interaction, json.dumps({"repo": "owner/repo", "number": 1, "action": action, "head": "a" * 40})
            ),
        )
    monkeypatch.setattr(github, "repositories", lambda _config: ["owner/repo"])
    monkeypatch.setattr(github, "fetch", lambda _repo, kind: [item()] if kind == "pr" else [])
    reply = Activity("reply/1", "2099-01-01T00:00:00Z", "reviewer", "github:commented", "", "Please revisit")
    monkeypatch.setattr(github, "activity", lambda _item: [reply])
    refreshed = service.refresh(db, output)[0]
    assert not refreshed.archived
    assert any(event.note == "Please revisit" for event in refreshed.events)
    repeated = service.refresh(db, output)[0]
    assert len(repeated.events) == len(refreshed.events)
    before = output.read_bytes()

    def fail_activity(_item: Item) -> list[Activity]:
        raise RuntimeError("GitHub unavailable")

    monkeypatch.setattr(github, "activity", fail_activity)
    with pytest.raises(RuntimeError, match="GitHub unavailable"):
        service.refresh(db, output)
    assert output.read_bytes() == before
    assert store.read_entries(db)[0] == repeated
