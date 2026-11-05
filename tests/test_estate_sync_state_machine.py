"""A Hypothesis state machine over the estate's desired -> plan -> apply path.

Desired state is the managed specs, the plan is `audit_estate`, and applying it is
`clean_estate_item` plus the router and code-workspace syncs. The machine declares and forgets
specs and puts clones, plain directories, files and worktrees in the estate root and an org
folder, in any order. After each audit, clean or sync it checks the rules below against a model
of what is on disk.

Clones are written by hand (HEAD, objects, refs and a config with an origin), which is enough for
`git config` and `git status` to treat them as repositories; `test_fake_clones_are_real_to_git`
proves that. Without that check, a remote nobody can read would make every clone "match".
"""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import sys
import tempfile
from dataclasses import dataclass

import pytest
from hypothesis import HealthCheck, settings
from hypothesis import strategies as st
from hypothesis.stateful import RuleBasedStateMachine, invariant, rule

from rig import proc
from rig.workspace.engine import (
    KNOWN_SYSTEM_ENTRIES,
    audit_estate,
    clean_estate_item,
    get_git_remote_url,
    normalize_git_url,
    sync_estate_code_workspace,
    sync_estate_router,
)
from rig.workspace.layout import project_dir
from rig.workspace.models import EstateItem, EstateItemKind, ManagedRepoSpec, WorkspaceAuditResult

pytestmark = [
    pytest.mark.ci,
    # Hundreds of git subprocesses per run; the logic is platform-neutral and runs on Linux CI.
    pytest.mark.skipif(sys.platform == "win32", reason="subprocess-heavy; covered by the Linux run"),
]

ORG = "rtkelly-labs"
NAMES = ("blog", "rig", "Diffra")
ROOT_NAMES = (*NAMES, "scratch", "temp")
OWNERS = ("rtkelly13", ORG, "someone-else")
URL_FORMS = (
    "git@github.com:{owner}/{name}.git",
    "https://github.com/{owner}/{name}",
    "https://github.com/{owner}/{name}.git",
    "ssh://git@github.com/{owner}/{name}.git",
)
WHERE = (None, ORG)  # None is the estate root


@dataclass(frozen=True)
class Clone:
    url: str
    dirty: bool


@dataclass(frozen=True)
class Plain:
    """A directory without .git."""


@dataclass(frozen=True)
class File:
    pass


@dataclass(frozen=True)
class Worktree:
    """A directory whose .git is a file, as in a linked worktree."""


Entry = Clone | Plain | File | Worktree


def write_entry(path: pathlib.Path, entry: Entry) -> None:
    if isinstance(entry, File):
        path.write_text("x\n", encoding="utf-8")
        return
    path.mkdir()
    if isinstance(entry, Worktree):
        (path / ".git").write_text("gitdir: /nowhere/.git/worktrees/x\n", encoding="utf-8")
    elif isinstance(entry, Clone):
        git = path / ".git"
        (git / "objects").mkdir(parents=True)
        (git / "refs" / "heads").mkdir(parents=True)
        (git / "HEAD").write_text("ref: refs/heads/main\n", encoding="utf-8")
        (git / "config").write_text(
            f'[core]\n\trepositoryformatversion = 0\n\tbare = false\n[remote "origin"]\n\turl = {entry.url}\n',
            encoding="utf-8",
        )
        if entry.dirty:
            (path / "wip.txt").write_text("unsaved\n", encoding="utf-8")


def remove_path(path: pathlib.Path) -> None:
    if path.is_dir() and not path.is_symlink():
        shutil.rmtree(path)
    elif path.exists():
        path.unlink()


def same_repo(a: str | None, b: str) -> bool:
    return normalize_git_url(a) == normalize_git_url(b)


def snapshot(base: pathlib.Path) -> list[tuple[str, bytes]]:
    files: list[tuple[str, bytes]] = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames.sort()
        here = pathlib.Path(dirpath)
        files.append((here.relative_to(base).as_posix() + "/", b""))
        files.extend((f"{(here / f).relative_to(base).as_posix()}", (here / f).read_bytes()) for f in sorted(filenames))
    return files


def test_fake_clones_are_real_to_git(tmp_path: pathlib.Path):
    """The machine's oracle depends on git reading these remotes and statuses."""
    url = "https://github.com/rtkelly13/blog"
    write_entry(tmp_path / "clean", Clone(url, dirty=False))
    write_entry(tmp_path / "dirty", Clone(url, dirty=True))
    assert get_git_remote_url(tmp_path / "clean") == url
    clean, dirty = (proc.git("status", "--porcelain", cwd=tmp_path / n) for n in ("clean", "dirty"))
    assert (clean.ok, clean.out.strip()) == (True, "")
    assert (dirty.ok, dirty.out.strip()) == (True, "?? wip.txt")


class EstateSync(RuleBasedStateMachine):
    def __init__(self) -> None:
        super().__init__()
        self.base = pathlib.Path(tempfile.mkdtemp(prefix="estate-"))
        self.root = self.base / "personal"
        self.root.mkdir()
        (self.base / ORG).mkdir()
        self.specs: dict[str, ManagedRepoSpec] = {}
        self.disk: dict[pathlib.Path, Entry] = {}

    def teardown(self) -> None:
        shutil.rmtree(self.base, ignore_errors=True)

    # -- desired state -------------------------------------------------------------------------

    @rule(name=st.sampled_from(NAMES), owner=st.sampled_from(OWNERS), enabled=st.booleans())
    def declare(self, name: str, owner: str, enabled: bool) -> None:
        self.specs[name] = ManagedRepoSpec(name=name, repo=f"git@github.com:{owner}/{name}.git", enabled=enabled)

    @rule(name=st.sampled_from(NAMES))
    def forget(self, name: str) -> None:
        self.specs.pop(name, None)

    # -- what is on disk -----------------------------------------------------------------------

    def _put(self, path: pathlib.Path, entry: Entry) -> None:
        remove_path(path)
        self.disk.pop(path, None)
        write_entry(path, entry)
        self.disk[path] = entry

    def _where(self, where: str | None) -> pathlib.Path:
        return self.root if where is None else self.base / where

    @rule(
        name=st.sampled_from(ROOT_NAMES),
        where=st.sampled_from(WHERE),
        owner=st.sampled_from(OWNERS),
        remote_name=st.sampled_from(NAMES),
        form=st.sampled_from(URL_FORMS),
        dirty=st.booleans(),
    )
    def clone(self, name: str, where: str | None, owner: str, remote_name: str, form: str, dirty: bool) -> None:
        self._put(self._where(where) / name, Clone(form.format(owner=owner, name=remote_name), dirty))

    @rule(name=st.sampled_from(ROOT_NAMES), where=st.sampled_from(WHERE))
    def mkdir(self, name: str, where: str | None) -> None:
        self._put(self._where(where) / name, Plain())

    @rule(name=st.sampled_from(ROOT_NAMES))
    def touch(self, name: str) -> None:
        self._put(self.root / name, File())

    @rule(name=st.sampled_from(ROOT_NAMES))
    def worktree(self, name: str) -> None:
        self._put(self.root / name, Worktree())

    @rule(name=st.sampled_from(ROOT_NAMES), where=st.sampled_from(WHERE))
    def remove(self, name: str, where: str | None) -> None:
        path = self._where(where) / name
        remove_path(path)
        self.disk.pop(path, None)

    # -- plan ----------------------------------------------------------------------------------

    def _audit(self) -> WorkspaceAuditResult:
        return audit_estate(self.root, list(self.specs.values()), skip=frozenset())

    def _expected_root_kind(self, name: str, entry: Entry) -> EstateItemKind:
        if name in KNOWN_SYSTEM_ENTRIES:
            return EstateItemKind.KNOWN_SYSTEM
        spec = self.specs.get(name)
        retained = spec is not None and not spec.enabled
        if isinstance(entry, File):
            return EstateItemKind.UNMANAGED_FILE
        if isinstance(entry, Worktree):
            return EstateItemKind.LINKED_WORKTREE
        if isinstance(entry, Plain):
            return EstateItemKind.RETAINED_ARCHIVE if retained else EstateItemKind.UNMANAGED_DIR
        if retained:
            return EstateItemKind.RETAINED_ARCHIVE
        if spec is not None and same_repo(entry.url, spec.repo):
            return EstateItemKind.MANAGED_REPO
        return EstateItemKind.UNMANAGED_REPO

    def _check_plan(self, audit: WorkspaceAuditResult) -> None:
        items = [
            *audit.managed_repos,
            *audit.linked_worktrees,
            *audit.unmanaged_items,
            *audit.known_system_items,
            *audit.retained_archives,
        ]
        in_root = [i for i in items if i.path.parent == self.root]
        on_disk = sorted(p.name for p in self.root.iterdir())

        # The audit partitions the root: every entry once, in the kind the model predicts.
        assert audit.total_items == len(on_disk)
        assert sorted(i.name for i in in_root) == on_disk
        expected = {p.name: self._expected_root_kind(p.name, e) for p, e in self.disk.items() if p.parent == self.root}
        assert {i.name: i.kind for i in in_root} == expected

        # Only org-folder clones are reported from outside the root, and only as managed.
        for item in items:
            if item.path.parent != self.root:
                assert item.kind is EstateItemKind.MANAGED_REPO
                assert isinstance(self.disk.get(item.path), Clone)

        # A repo called managed carries the remote its spec declares, wherever it lives.
        for item in audit.managed_repos:
            assert same_repo(item.remote_url, self.specs[item.name].repo), item

        # Every enabled spec is accounted for: managed, missing, or its directory is reported.
        managed = {i.name for i in audit.managed_repos}
        missing = {s.name for s in audit.missing_managed_repos}
        reported = {i.path for i in items}
        for spec in self.specs.values():
            if not spec.enabled:
                assert spec.name not in missing
                continue
            expected_dir = project_dir(self.root, spec.name, spec.repo)
            assert (spec.name in managed) + (spec.name in missing) <= 1
            if spec.name in missing:
                assert not expected_dir.exists()
            elif spec.name not in managed:
                assert expected_dir in reported or self.root / spec.name in reported, spec

    @rule()
    def audit(self) -> None:
        first = self._audit()
        self._check_plan(first)
        assert self._audit() == first  # the plan is a pure function of disk and specs

    # -- apply ---------------------------------------------------------------------------------

    @rule(dry_run=st.booleans())
    def clean(self, dry_run: bool) -> None:
        audit = self._audit()
        self._check_plan(audit)
        before = snapshot(self.base)
        protected: list[EstateItem] = [*audit.managed_repos, *audit.known_system_items, *audit.retained_archives]
        for item in protected:
            assert not clean_estate_item(item, dry_run=dry_run).ok
        refused = {i.path for i in audit.unmanaged_items if not clean_estate_item(i, dry_run=dry_run).ok}

        if dry_run:
            assert snapshot(self.base) == before
            assert not refused
            return

        for item in protected:
            assert item.path.exists()
        for path, entry in self.disk.items():
            if isinstance(entry, Clone) and entry.dirty:
                assert path.exists(), f"clean deleted a dirty clone: {path}"
        self.disk = {p: e for p, e in self.disk.items() if p.exists()}
        assert {i.path for i in self._audit().unmanaged_items} <= refused

    @rule()
    def sync(self) -> None:
        specs = list(self.specs.values())
        router = sync_estate_router(self.root, specs)
        code_ws = sync_estate_code_workspace(self.root, specs)
        first = (router.read_bytes(), code_ws.read_bytes())
        sync_estate_router(self.root, specs)
        sync_estate_code_workspace(self.root, specs)
        assert (router.read_bytes(), code_ws.read_bytes()) == first, "sync is not idempotent"
        self.disk[router] = File()
        self.disk[code_ws] = File()

        router_text = first[0].decode("utf-8")
        for spec in specs:
            assert (f"[`{spec.name}`]" in router_text) == spec.enabled
        folders = json.loads(first[1])["folders"]
        assert len({f["name"] for f in folders}) == len(folders)
        for folder in folders:
            assert (self.root / folder["path"]).exists()
        # Everything the plan calls managed is open in the editor workspace.
        assert {i.name for i in self._audit().managed_repos} <= {f["name"] for f in folders}

    @invariant()
    def model_matches_disk(self) -> None:
        on_disk = {p for parent in (self.root, self.base / ORG) for p in parent.iterdir()}
        assert on_disk == set(self.disk)


EstateSync.TestCase.settings = settings(
    stateful_step_count=25,
    deadline=None,
    suppress_health_check=[HealthCheck.too_slow],
)
TestEstateSync = EstateSync.TestCase
