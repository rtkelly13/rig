"""The estate's routes in `rig web`: decision feed, disk, trim scan cache and the guarded apply."""

from __future__ import annotations

import json
import os
import threading
import time
import urllib.error
import urllib.request
from collections.abc import Iterator
from dataclasses import asdict, dataclass
from pathlib import Path

import pytest

from rig import proc
from rig.estate import trim
from rig.estate.decisions import Decision
from rig.estate.disksize import Measure
from rig.estate.trim import Trimmable
from rig.estate.web import TrimCache, make_route, read_decisions
from rig.web_host.server import StudioServer
from tests.links import dir_link

pytestmark = pytest.mark.ci

DAY = 86400.0
JSON = {"Content-Type": "application/json"}


@dataclass(frozen=True, slots=True)
class Reply:
    status: int
    body: str


def call(url: str, *, body: bytes | None = None, headers: dict[str, str] | None = None) -> Reply:
    request = urllib.request.Request(url, data=body, headers=headers or {}, method="POST" if body else "GET")
    try:
        with urllib.request.urlopen(request, timeout=10) as response:  # noqa: S310 - loopback test server
            return Reply(response.status, response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        return Reply(error.code, error.read().decode("utf-8"))


def _item(worktree: Path, name: str, *, refused: str = "", freeable: int = 4096) -> Trimmable:
    path = worktree / name
    path.mkdir(parents=True, exist_ok=True)
    (path / "blob").write_bytes(b"x" * 16)
    return Trimmable(worktree, path, "node_modules", Measure(freeable, freeable), refused)


@dataclass
class FakeEstate:
    """A scan that returns fixed items, so a cache can be driven without git."""

    items: list[Trimmable]

    def scan(self, _clone: Path) -> list[Trimmable]:
        return list(self.items)


def _cache(tmp_path: Path, items: list[Trimmable], *, recheck: str = "") -> TrimCache:
    estate = FakeEstate(items)
    return TrimCache(
        root=lambda: tmp_path,
        clones=lambda _root: [tmp_path / "clone"],
        scan=estate.scan,
        recheck=lambda _item: recheck,
        clock=lambda: "2026-11-05T09:00:00+00:00",
    )


def _scanned(cache: TrimCache) -> None:
    _ = cache.current()
    cache.wait(10)


# --- decisions --------------------------------------------------------------------------------------------------------


def _decision(path: str) -> Decision:
    return Decision("no-pr-worktree", "rig", path, "idle", 10, "remove it", f"git worktree remove {path}")


def write_feed(path: Path, decisions: list[Decision]) -> None:
    """The feed as `rig estate decisions --write` leaves it."""
    path.parent.mkdir(parents=True, exist_ok=True)
    _ = path.write_text(json.dumps([asdict(d) for d in decisions], indent=2) + "\n", encoding="utf-8")


def test_a_missing_feed_is_an_empty_list_with_a_note(tmp_path: Path) -> None:
    feed = read_decisions(tmp_path / "decisions.json")
    assert feed.decisions == ()
    assert feed.written == ""
    assert "has not written" in feed.note


def test_the_feed_is_read_with_its_mtime_as_written(tmp_path: Path) -> None:
    path = tmp_path / "decisions.json"
    write_feed(path, [_decision("C:/code/a")])
    os.utime(path, (1_700_000_000, 1_700_000_000))
    feed = read_decisions(path)
    assert [d.path for d in feed.decisions] == ["C:/code/a"]
    assert feed.written == "2023-11-14T22:13:20+00:00"
    assert feed.note == ""


def test_an_empty_or_malformed_feed_says_why_nothing_shows(tmp_path: Path) -> None:
    path = tmp_path / "decisions.json"
    write_feed(path, [])
    assert read_decisions(path).note == "Nothing needs deciding."
    _ = path.write_text("{not json", encoding="utf-8")
    broken = read_decisions(path)
    assert broken.decisions == ()
    assert "expected shape" in broken.note
    assert broken.written


# --- trim cache -------------------------------------------------------------------------------------------------------


def test_the_first_read_starts_a_scan_and_returns_at_once(tmp_path: Path) -> None:
    release = threading.Event()
    item = _item(tmp_path / "wt", "node_modules")

    def slow_scan(_clone: Path) -> list[Trimmable]:
        _ = release.wait(10)
        return [item]

    cache = TrimCache(root=lambda: tmp_path, clones=lambda _r: [tmp_path], scan=slow_scan, clock=lambda: "then")
    first = cache.current()
    assert first.scanning
    assert first.candidates == ()
    assert cache.current().scanning  # no second scan while one runs
    release.set()
    cache.wait(10)
    done = cache.current()
    assert not done.scanning
    assert done.scanned_at == "then"
    assert [c.path for c in done.candidates] == [str(item.path)]


def test_a_failed_scan_reports_its_error_and_keeps_the_last_list(tmp_path: Path) -> None:
    item = _item(tmp_path / "wt", "node_modules")
    calls: list[int] = []

    def flaky(_clone: Path) -> list[Trimmable]:
        calls.append(1)
        if len(calls) > 1:
            raise OSError("disk went away")
        return [item]

    cache = TrimCache(root=lambda: tmp_path, clones=lambda _r: [tmp_path], scan=flaky, clock=lambda: "then")
    _scanned(cache)
    _ = cache.current(refresh=True)
    cache.wait(10)
    after = cache.current()
    assert after.error == "disk went away"
    assert [c.path for c in after.candidates] == [str(item.path)]


def test_apply_removes_only_offered_unrefused_paths(tmp_path: Path) -> None:
    wt = tmp_path / "wt"
    good = _item(wt, "node_modules")
    refused = _item(wt, ".venv", refused="contains tracked files")
    cache = _cache(tmp_path, [good, refused])

    before_scan = cache.apply([str(good.path)])
    assert before_scan[0].status == "not-in-scan"  # nothing is offered until a scan finishes
    _scanned(cache)

    results = cache.apply([str(good.path), str(refused.path), str(wt / "elsewhere"), str(good.path)])
    assert [(r.path, r.status) for r in results] == [
        (str(good.path), "removed"),
        (str(refused.path), "refused: contains tracked files"),
        (str(wt / "elsewhere"), "not-in-scan"),
    ]
    assert results[0].freeable == 4096
    assert not good.path.exists()
    assert refused.path.exists()


def test_apply_refuses_what_the_recheck_refuses(tmp_path: Path) -> None:
    item = _item(tmp_path / "wt", "node_modules")
    cache = _cache(tmp_path, [item], recheck="worktree is in use again")
    _scanned(cache)
    assert cache.apply([str(item.path)])[0].status == "refused: worktree is in use again"
    assert item.path.exists()


def test_apply_marks_the_cache_stale(tmp_path: Path) -> None:
    item = _item(tmp_path / "wt", "node_modules")
    cache = _cache(tmp_path, [item])
    _scanned(cache)
    _ = cache.apply([str(item.path)])
    shown = cache.current()
    assert shown.scanning  # the next read rescans
    assert str(item.path) not in [c.path for c in shown.candidates]
    cache.wait(10)


# --- recheck against a real worktree ----------------------------------------------------------------------------------


def _git(args: list[str], cwd: Path) -> str:
    return proc.git(*args, cwd=cwd, check=True).out.strip()


@pytest.fixture
def idle_worktree(tmp_path: Path) -> Path:
    clone = tmp_path / "clone"
    clone.mkdir()
    _git(["init", "-q", "-b", "main"], clone)
    _git(["config", "user.email", "t@example.com"], clone)
    _git(["config", "user.name", "T"], clone)
    _ = (clone / ".gitignore").write_text("node_modules/\n", encoding="utf-8")
    _git(["add", "."], clone)
    _git(["commit", "-qm", "first"], clone)
    tree = tmp_path / "idle"
    _git(["worktree", "add", "-q", "-b", "idle", str(tree)], clone)
    week_ago = time.time() - 8 * DAY
    admin = Path(_git(["rev-parse", "--absolute-git-dir"], tree))
    for name in ("index", "HEAD", "logs/HEAD"):
        if (admin / name).exists():
            os.utime(admin / name, (week_ago, week_ago))
    modules = tree / "node_modules"
    modules.mkdir()
    (modules / "blob").write_bytes(b"x" * 8192)
    return tree


def test_recheck_allows_an_unchanged_candidate(idle_worktree: Path) -> None:
    [item] = trim.scan(idle_worktree.parent / "clone", floor=0)
    assert trim.recheck(item) == ""


def test_recheck_refuses_a_candidate_that_became_a_link(idle_worktree: Path, tmp_path: Path) -> None:
    clone = idle_worktree.parent / "clone"
    cache = TrimCache(
        root=lambda: tmp_path,
        clones=lambda _r: [clone],
        scan=lambda c: trim.scan(c, floor=0),
        clock=lambda: "now",
    )
    _scanned(cache)
    [item] = trim.scan(clone, floor=0)
    trim.remove_tree(item.path)
    target = tmp_path / "shared-store"
    target.mkdir()
    _ = (target / "keep").write_text("shared", encoding="utf-8")
    _ = dir_link(item.path, target)

    assert cache.apply([str(item.path)])[0].status == "refused: is a link"
    assert (target / "keep").read_text(encoding="utf-8") == "shared"


def test_recheck_refuses_a_worktree_used_since_the_scan(idle_worktree: Path) -> None:
    [item] = trim.scan(idle_worktree.parent / "clone", floor=0)
    _ = _git(["status"], idle_worktree)
    _git(["commit", "-q", "--allow-empty", "-m", "back at it"], idle_worktree)
    assert trim.recheck(item) == "worktree is in use again"


def test_recheck_refuses_a_worktree_locked_since_the_scan(idle_worktree: Path) -> None:
    clone = idle_worktree.parent / "clone"
    [item] = trim.scan(clone, floor=0)
    _ = _git(["worktree", "lock", str(idle_worktree)], clone)
    assert trim.recheck(item) == "worktree is locked"
    assert item.path.is_dir()


# --- end to end through the server ------------------------------------------------------------------------------------


@pytest.fixture
def served(tmp_path: Path) -> Iterator[str]:
    feed = tmp_path / "feed" / "decisions.json"
    write_feed(feed, [_decision("C:/code/a")])
    wt = tmp_path / "wt"
    cache = _cache(tmp_path, [_item(wt, "node_modules"), _item(wt, ".venv", refused="not gitignored")])
    server = StudioServer(
        port=0,
        db_path=tmp_path / "rig.db",
        routes=(make_route(cache, feed_path=lambda: feed, root=lambda: tmp_path),),
    )
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()
        cache.wait(10)


def test_the_estate_routes_end_to_end(served: str, tmp_path: Path) -> None:
    decisions = json.loads(call(f"{served}/api/ops/decisions").body)
    assert set(decisions) == {"decisions", "written", "note"}
    assert decisions["decisions"][0]["command"] == "git worktree remove C:/code/a"

    disk = json.loads(call(f"{served}/api/ops/estate/disk").body)
    assert disk["path"] == str(tmp_path)
    assert disk["total"] >= disk["free"] > 0

    first = json.loads(call(f"{served}/api/ops/estate/trim").body)
    assert set(first) == {"scanned_at", "candidates", "scanning", "error"}
    deadline = time.monotonic() + 10
    scan = first
    while scan["scanning"] and time.monotonic() < deadline:
        time.sleep(0.05)
        scan = json.loads(call(f"{served}/api/ops/estate/trim").body)
    assert {c["path"]: c["refused"] for c in scan["candidates"]} == {
        str(tmp_path / "wt" / "node_modules"): "",
        str(tmp_path / "wt" / ".venv"): "not gitignored",
    }

    body = json.dumps({"paths": [str(tmp_path / "wt" / "node_modules"), str(tmp_path / "wt" / ".venv")]}).encode()
    reply = call(f"{served}/api/ops/estate/trim/apply", body=body, headers=JSON)
    assert reply.status == 200
    assert [r["status"] for r in json.loads(reply.body)] == ["removed", "refused: not gitignored"]
    assert not (tmp_path / "wt" / "node_modules").exists()


@pytest.mark.parametrize(
    ("body", "headers"),
    [
        (b"paths=x", {"Content-Type": "application/x-www-form-urlencoded"}),
        (b"{not json", JSON),
        (b'{"paths": "C:/code"}', JSON),
        (b'{"paths": ["x"]}', {**JSON, "Origin": "http://evil.example"}),
    ],
    ids=["form-post", "malformed", "wrong-shape", "cross-origin"],
)
def test_a_bad_apply_is_refused_without_touching_anything(
    served: str, tmp_path: Path, body: bytes, headers: dict[str, str]
) -> None:
    reply = call(f"{served}/api/ops/estate/trim/apply", body=body, headers=headers)
    assert reply.status == 400
    assert set(json.loads(reply.body)) == {"error"}
    assert (tmp_path / "wt" / "node_modules").exists()


def test_unknown_estate_paths_are_404_and_other_paths_pass_through(served: str) -> None:
    assert call(f"{served}/api/ops/estate/nope").status == 404
    assert call(f"{served}/api/ops/estate/trim/apply").status == 404  # GET on the write endpoint
    assert call(f"{served}/api/ops/other").status == 404
