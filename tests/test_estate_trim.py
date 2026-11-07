from __future__ import annotations

import os
import pathlib
import stat
import subprocess
import time

import pytest

from desktop_manager.jobs.registry import Cadence, Environment, default_jobs
from rig.estate import trim
from rig.estate.disksize import measure

DAY = 86400.0


def _git(args: list[str], cwd: pathlib.Path) -> str:
    return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True, text=True).stdout.strip()


@pytest.fixture
def repo(tmp_path: pathlib.Path) -> pathlib.Path:
    root = tmp_path / "clone"
    root.mkdir()
    _git(["init", "-q", "-b", "main"], root)
    _git(["config", "user.email", "t@example.com"], root)
    _git(["config", "user.name", "T"], root)
    (root / ".gitignore").write_text("node_modules/\n.venv/\nbin/\nobj/\ntemp/\n", encoding="utf-8")
    (root / "app").mkdir()
    (root / "app" / "App.csproj").write_text("<Project />\n", encoding="utf-8")
    (root / "scripts" / "bin").mkdir(parents=True)
    (root / "scripts" / "bin" / "run.sh").write_text("echo hi\n", encoding="utf-8")
    (root / "vendor" / "node_modules").mkdir(parents=True)
    (root / "vendor" / "node_modules" / "patched.js").write_text("// kept\n", encoding="utf-8")
    _git(["add", "."], root)
    _git(["add", "-f", "scripts/bin/run.sh", "vendor/node_modules/patched.js"], root)
    _git(["commit", "-qm", "first"], root)
    return root


def _fill(path: pathlib.Path) -> pathlib.Path:
    path.mkdir(parents=True, exist_ok=True)
    (path / "blob").write_bytes(b"x" * 4096)
    return path


def _worktree(repo: pathlib.Path, name: str) -> pathlib.Path:
    path = repo.parent / name
    _git(["worktree", "add", "-q", "-b", name, str(path)], repo)
    return path


def _touch_bookkeeping(worktree: pathlib.Path, when: float) -> None:
    admin = pathlib.Path(_git(["rev-parse", "--absolute-git-dir"], worktree))
    for name in ("index", "HEAD", "logs/HEAD"):
        if (admin / name).exists():
            os.utime(admin / name, (when, when))


def test_last_activity_reads_git_bookkeeping_not_tree_mtimes(repo: pathlib.Path) -> None:
    tree = _worktree(repo, "spike")
    week_ago = time.time() - 8 * DAY
    _touch_bookkeeping(tree, week_ago)
    _ = _fill(tree / "node_modules")  # a fresh build must not make the branch look used

    assert abs(trim.last_activity(tree) - week_ago) < 1


def test_unreadable_bookkeeping_counts_as_active(tmp_path: pathlib.Path) -> None:
    (tmp_path / ".git").write_text("not a pointer", encoding="utf-8")
    assert time.time() - trim.last_activity(tmp_path) < 60


def test_scan_trims_only_idle_unlocked_worktrees(repo: pathlib.Path) -> None:
    later = time.time() + 8 * DAY
    idle = _worktree(repo, "idle")
    busy = _worktree(repo, "busy")
    locked = _worktree(repo, "locked")
    _git(["worktree", "lock", str(locked)], repo)
    _touch_bookkeeping(busy, later - 3600)
    for tree in (idle, busy, locked):
        _ = _fill(tree / "node_modules")
    _ = _fill(repo / "node_modules")  # the main checkout is reclaim's business, not trim's

    found = trim.scan(repo, floor=0, now=later)

    assert {(t.path, t.category, t.refused) for t in found} == {
        (idle / "node_modules", "node_modules", ""),
        (idle / "vendor" / "node_modules", "node_modules", "contains tracked files"),
    }


def test_each_directory_proves_its_own_safety(repo: pathlib.Path) -> None:
    tree = _worktree(repo, "spike")
    _ = _fill(tree / "app" / "bin")  # beside App.csproj: build output
    _ = _fill(tree / "scripts" / "bin" / "more")  # no project file beside it: a bin of scripts
    _ = _fill(tree / "vendor" / "node_modules")  # a vendored patch someone committed
    _ = _fill(tree / "lib" / "obj")  # no project file: not build output
    _ = _fill(tree / "temp" / "node_modules")  # the session's own output is never walked
    _ = _fill(tree / "web" / ".venv" / "nested")
    _ = _fill(tree / "web" / ".venv" / "nested" / ".git")

    found = {
        t.path.relative_to(tree).as_posix(): t.refused for t in trim.scan(repo, floor=0, now=time.time() + 8 * DAY)
    }

    assert found == {
        "app/bin": "",
        "vendor/node_modules": "contains tracked files",
        "web/.venv": "holds a git checkout",
    }


def test_remove_deletes_read_only_files_and_refuses_refused(repo: pathlib.Path) -> None:
    tree = _worktree(repo, "spike")
    target = _fill(tree / "node_modules")
    (target / "blob").chmod(stat.S_IREAD)
    item = trim.Trimmable(tree, target, "node_modules", measure(target))

    assert trim.remove(trim.Trimmable(tree, target, "node_modules", measure(target), "not gitignored")) == "refused"
    assert target.exists()
    assert trim.remove(item) == "removed"
    assert not target.exists()


def test_estate_jobs_scan_the_code_root_above_the_personal_clones_not_the_runtime_copy(
    tmp_path: pathlib.Path,
) -> None:
    root = tmp_path / "code"
    env = Environment(
        scripts=tmp_path / "runtime" / "Scripts",
        plan=tmp_path / "runtime" / "plan.json",
        workspace_root=tmp_path / "ws",
        personal_root=root / "personal",
        work_roots=(root / "acme",),
        decisions_feed=tmp_path / "decisions.json",
        orca_user_data=tmp_path / "orca-personal",
    )
    jobs = {j.name: j for j in default_jobs(env)}

    assert jobs["estate-trim"].argv[1:] == ("trim", "--apply", "--root", str(root))
    assert jobs["estate-trim"].cadence is Cadence.DAILY
    assert jobs["estate-sync"].argv[1:] == ("sync", "--root", str(root))
    assert jobs["estate-risk"].argv[1:] == ("risk", "--root", str(root))


def test_remove_tree_reaches_past_the_windows_path_limit(tmp_path: pathlib.Path) -> None:
    root = tmp_path / "node_modules"
    deep = root
    while len(str(deep)) < 300:
        deep = deep / ("d" * 40)
    _ = pathlib.Path(trim.long_path(deep)).mkdir(parents=True)
    leaf = pathlib.Path(trim.long_path(deep / "index.js"))
    _ = leaf.write_text("x")
    leaf.chmod(stat.S_IREAD)

    trim.remove_tree(root)

    assert not root.exists()


@pytest.mark.skipif(os.name != "nt", reason="ACLs are Windows-only")
def test_remove_tree_unlocks_a_directory_its_owner_cannot_list(tmp_path: pathlib.Path) -> None:
    root = tmp_path / "wt"
    cache = root / ".pytest_cache" / "v"
    cache.mkdir(parents=True)
    _ = (cache / "lastfailed").write_text("{}")
    locked = str(root / ".pytest_cache")
    owner_rights = "*S-1-3-4"
    for change in (
        ["/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F"],
        ["/remove:g", owner_rights],
    ):
        _ = subprocess.run(["icacls", locked, *change], capture_output=True, check=True)
    try:
        _ = list(os.scandir(root / ".pytest_cache"))
    except PermissionError:
        pass
    else:
        pytest.skip("an elevated process reads the directory through Administrators")

    trim.remove_tree(root)

    assert not root.exists()


@pytest.mark.skipif(os.name != "nt", reason="ACLs are Windows-only")
def test_remove_tree_deletes_a_file_whose_acl_denies_delete(tmp_path: pathlib.Path) -> None:
    root = tmp_path / "venv"
    site = root / "site-packages"
    site.mkdir(parents=True)
    probe = site / "probe.py"
    locked = site / "from_thread.py"
    sid = trim._user_sid()  # pyright: ignore[reportPrivateUsage]
    for change in (["/inheritance:r", "/grant:r", f"*{sid}:(OI)(CI)M"], ["/remove:g", "*S-1-3-4"]):
        _ = subprocess.run(["icacls", str(site), *change], capture_output=True, check=True)
    for target in (probe, locked):
        _ = target.write_text("x")
        _ = subprocess.run(
            ["icacls", str(target), "/inheritance:r", "/grant:r", "*S-1-5-18:F", "*S-1-5-32-544:F"],
            capture_output=True,
            check=True,
        )
    try:
        probe.unlink()
    except PermissionError:
        pass
    else:
        pytest.skip("an elevated process deletes the file through Administrators")

    trim.remove_tree(root)

    assert not root.exists()


@pytest.mark.skipif(os.name != "nt", reason="the prefix is Windows-only")
def test_long_path_prefixes_once() -> None:
    once = trim.long_path(pathlib.Path("C:/code/x"))

    assert once == "\\\\?\\C:\\code\\x"
    assert trim.long_path(pathlib.Path(once)) == once
