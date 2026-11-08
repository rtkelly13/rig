"""Orca's project sidebar, held to the layout `managed-projects.json` declares.

Each managed project may carry `"orca": {"group": "personal/knowledge"}`: the
group, and any subgroups, it belongs under in Orca. Groups are `/`-joined
names from the top. A top-level group is normally Orca's folder-scan group
for a code-root folder (`~/code/rtkelly-labs`), and its subgroups are the
ones made by hand. `"group": ""` puts a project at the top level, in no
group. A project without the field is not meant to be in Orca.

| kind | what it means | `--apply` |
|---|---|---|
| `unregistered` | declared and cloned, but not in Orca | registers it, then moves it |
| `wrong-group` | in Orca, but under another group or none | moves it |
| `missing-group` | a declared group Orca does not have | creates it |
| `undeclared` | in Orca, but its managed-projects entry has no `orca.group` | nothing: a config fix |
| `unmanaged` | in Orca, owned by an estate account, not in managed-projects | nothing: a config fix |
| `stale` | in Orca, but its folder is gone | nothing |
| `no-clone` | declared, but neither in Orca nor cloned where its group expects | nothing |
| `archived` | in Orca, but its repo is archived | removes it, unless a process works in it |
| `archived-clone` | an archived repo still cloned locally | nothing: delete it once nothing is only there |
| `empty-group` | a group holding no project or folder workspace, not declared | deletes it |
| `in-work-profile` | an estate repo in the work Orca profile (Windows) | removes it from that profile |
| `work-empty-group` | a work-profile group that only held estate repos | deletes it once they are gone |
| `skipped` | in Orca, but listed in this machine's `tools.estate.skip_repos` | nothing: remove it by hand |

A project in `tools.estate.skip_repos` (`~/.workspace/config.json`) is never
registered on this machine and never reported as unregistered or missing a clone.

An archived repo is finished, so it never belongs in Orca, and it is never reported
as unregistered or missing a clone. GitHub is asked which repos are archived
(`github_archived`); when it cannot be asked, only `archived-projects.json` counts.

Only the personal estate counts (`scopes.estate_owners`). Work repos keep their
hand-made Orca layout, and the audit never reports them. On Windows the estate
has its own Orca profile (`orca_runtime.estate_user_data`); the default profile
is the work one, and estate repos are taken out of it (`audit_work`).

Orca's state is read, and changed, through the running app's runtime API
(`orca_runtime`), the same one its CLI and mobile app use.
"""

from __future__ import annotations

import pathlib
from collections.abc import Callable, Iterable
from dataclasses import dataclass, field
from enum import StrEnum

from rig import roots, scopes
from rig.estate import busy
from rig.estate.orca_runtime import Group, OrcaRuntime, Remote, Repo
from rig.repo_slug import RepoSlug
from rig.typed_json import decode_json
from rig.workspace_config import skip_reason


class Kind(StrEnum):
    UNREGISTERED = "unregistered"
    WRONG_GROUP = "wrong-group"
    MISSING_GROUP = "missing-group"
    UNDECLARED = "undeclared"
    UNMANAGED = "unmanaged"
    STALE = "stale"
    NO_CLONE = "no-clone"
    ARCHIVED = "archived"
    ARCHIVED_CLONE = "archived-clone"
    EMPTY_GROUP = "empty-group"
    IN_WORK_PROFILE = "in-work-profile"
    SKIPPED = "skipped"
    WORK_EMPTY_GROUP = "work-empty-group"


#: The kinds `--apply` can close by itself.
APPLIABLE = frozenset(
    {
        Kind.UNREGISTERED,
        Kind.WRONG_GROUP,
        Kind.MISSING_GROUP,
        Kind.ARCHIVED,
        Kind.EMPTY_GROUP,
        Kind.IN_WORK_PROFILE,
        Kind.WORK_EMPTY_GROUP,
    }
)


@dataclass(frozen=True, slots=True)
class Finding:
    """One way Orca differs from the declared layout, and how to close the gap."""

    kind: str
    repo: str
    path: str
    reason: str
    suggested_action: str
    #: A command that carries out the action, or "" when it is a config fix.
    command: str


# ---------------------------------------------------------------- the declared layout


@dataclass(frozen=True, slots=True)
class _Placement:
    #: `/`-joined group path; "" for the top level.
    group: str = ""


@dataclass(frozen=True, slots=True)
class _Entry:
    name: str
    repo: str
    enabled: bool = True
    orca: _Placement | None = None


@dataclass(frozen=True, slots=True)
class _Managed:
    projects: tuple[_Entry, ...] = ()


@dataclass(frozen=True, slots=True)
class _Retired:
    name: str
    repo: str


@dataclass(frozen=True, slots=True)
class _Archive:
    projects: tuple[_Retired, ...] = ()


ARCHIVED_PROJECTS = roots.ARCHIVED_PROJECTS


@dataclass(frozen=True, slots=True)
class Declared:
    """A managed project's place in Orca: a group path, "" for the top level, None for not in Orca."""

    name: str
    repo: str
    #: `owner/name` as written in the repo URL.
    slug: str
    group: str | None
    #: Listed in archived-projects.json; GitHub's own archive flag is checked separately.
    archived: bool = False


def load_declared(config: pathlib.Path) -> list[Declared]:
    """The enabled GitHub projects in *config*, plus those in the archived-projects.json beside it.

    Archived projects are declared so a clone or registration left behind is still reported. A malformed
    file raises `ValueError`.
    """
    managed = decode_json(_Managed, config.read_text(encoding="utf-8"))
    declared: list[Declared] = []
    for entry in managed.projects:
        parsed = RepoSlug.from_remote(entry.repo)
        if entry.enabled and parsed is not None:
            group = entry.orca.group.strip("/") if entry.orca else None
            declared.append(Declared(entry.name, entry.repo, parsed.full_name, group))
    archive = config.with_name(ARCHIVED_PROJECTS)
    if archive.is_file():
        for retired in decode_json(_Archive, archive.read_text(encoding="utf-8")).projects:
            parsed = RepoSlug.from_remote(retired.repo)
            if parsed is not None:
                declared.append(Declared(retired.name, retired.repo, parsed.full_name, None, archived=True))
    return declared


# ---------------------------------------------------------------- Orca's state


@dataclass(frozen=True, slots=True)
class Registration:
    """A repo registered in Orca."""

    id: str
    name: str
    path: pathlib.Path
    #: `owner/name` from the remote, lowercase; None for a folder without a GitHub remote.
    slug: str | None
    group_id: str | None


def _slug_of(remote: Remote | None) -> str | None:
    slug = RepoSlug.from_remote(remote.canonical_key) if remote else None
    return slug.key if slug else None


def registration(repo: Repo) -> Registration:
    path = pathlib.Path(repo.path)
    return Registration(repo.id, repo.display_name or path.name, path, _slug_of(repo.remote), repo.group_id)


@dataclass(frozen=True, slots=True)
class Groups:
    """Orca's project groups, addressed by their `/`-joined path."""

    rows: tuple[Group, ...]
    _by_id: dict[str, Group] = field(init=False, repr=False)

    def __post_init__(self) -> None:
        object.__setattr__(self, "_by_id", {row.id: row for row in self.rows})

    def path_of(self, group_id: str | None) -> str | None:
        """The group's path, or None for no group or one Orca no longer has."""
        names: list[str] = []
        seen: set[str] = set()
        current = group_id
        while current is not None and current not in seen:
            row = self._by_id.get(current)
            if row is None:
                return None
            seen.add(current)
            names.append(row.name)
            current = row.parent_id
        return "/".join(reversed(names)) or None

    def lineage(self, group_id: str | None) -> tuple[str, ...]:
        """The group's id and each ancestor's, nearest first; empty for no group or one Orca no longer has."""
        ids: list[str] = []
        current = group_id
        while current is not None and current not in ids and current in self._by_id:
            ids.append(current)
            current = self._by_id[current].parent_id
        return tuple(ids)

    def id_of(self, path: str) -> str | None:
        return next((row.id for row in self.rows if self.path_of(row.id) == path), None)

    def has(self, path: str) -> bool:
        return self.id_of(path) is not None

    def folder(self, top: str) -> pathlib.Path | None:
        """The code-root folder a top-level group was scanned from."""
        for row in self.rows:
            if row.parent_id is None and row.name == top and row.parent_path:
                return pathlib.Path(row.parent_path)
        return None


@dataclass(frozen=True, slots=True)
class Snapshot:
    """What Orca holds right now."""

    registrations: tuple[Registration, ...]
    groups: Groups
    #: The groups holding a folder workspace: deleting one would delete the workspace too.
    workspace_groups: frozenset[str] = frozenset()


def snapshot(runtime: OrcaRuntime) -> Snapshot:
    return Snapshot(
        tuple(registration(r) for r in runtime.repos()),
        Groups(runtime.groups()),
        frozenset(w.group_id for w in runtime.folder_workspaces() if w.group_id),
    )


def empty_groups(
    state: Snapshot,
    *,
    keep: Iterable[str] = (),
    gone: Iterable[str] = (),
    within: Iterable[str] | None = None,
) -> list[Group]:
    """The outermost groups with no project or folder workspace anywhere below them.

    *keep* holds group paths never reported, *gone* registration ids to count as
    already removed, and *within*, when given, the only group ids that may be reported.
    """
    groups = state.groups
    removed = set(gone)
    occupied: set[str] = set()
    for found in state.registrations:
        if found.id not in removed:
            occupied.update(groups.lineage(found.group_id))
    for group_id in state.workspace_groups:
        occupied.update(groups.lineage(group_id))
    kept = set(keep)
    allowed = None if within is None else set(within)
    candidates = {
        row.id
        for row in groups.rows
        if row.id not in occupied and groups.path_of(row.id) not in kept and (allowed is None or row.id in allowed)
    }
    return [row for row in groups.rows if row.id in candidates and row.parent_id not in candidates]


def _unskipped(projects: Iterable[Declared], skipped: frozenset[str]) -> list[Declared]:
    return [p for p in projects if p.name not in skipped]


def _kept_paths(projects: Iterable[Declared], archived: frozenset[str]) -> set[str]:
    """Each live declared group and every group above it."""
    kept: set[str] = set()
    for project in projects:
        if project.group and not project.archived and project.slug.lower() not in archived:
            names = project.group.split("/")
            kept.update("/".join(names[:depth]) for depth in range(1, len(names) + 1))
    return kept


# ---------------------------------------------------------------- the comparison


def expected_clone(project: Declared, groups: Groups, code_root: pathlib.Path) -> pathlib.Path:
    """Where a declared project's clone belongs: its top group's folder, then its repo name.

    A top-level project has no group folder, so the first code-root folder holding
    a clone of that name wins, or the code root itself when none does.
    """
    name = project.slug.split("/", maxsplit=1)[-1]
    top = (project.group or "").split("/", maxsplit=1)[0]
    if top:
        return (groups.folder(top) or code_root / top) / name
    candidates = sorted(child / name for child in code_root.iterdir() if child.is_dir()) if code_root.is_dir() else []
    return next((c for c in candidates if c.is_dir()), code_root / name)


@dataclass(slots=True)
class _Audit:
    groups: Groups
    code_root: pathlib.Path
    exists: Callable[[pathlib.Path], bool]
    findings: list[Finding] = field(default_factory=list[Finding])
    missing: set[str] = field(default_factory=set[str])

    def add(self, kind: Kind, repo: str, path: pathlib.Path | str, reason: str, action: str, command: str = "") -> None:
        shown = path.as_posix() if isinstance(path, pathlib.Path) else path
        self.findings.append(Finding(kind.value, repo, shown, reason, action, command))

    def retired(self, project: Declared, found: Registration | None) -> None:
        """An archived project: out of Orca, and its clone flagged for cleanup."""
        if found is not None:
            self.add(
                Kind.ARCHIVED,
                project.name,
                found.path,
                f"in Orca, but {project.slug} is archived",
                "remove it from Orca",
                "estate orca --apply",
            )
        clone = found.path if found is not None else expected_clone(project, self.groups, self.code_root)
        if self.exists(clone):
            self.findings.append(_archived_clone(project.name, clone, project.slug))

    def skipped(self, project: Declared, found: Registration | None) -> None:
        """A project this machine skips: never registered, and a registration already there is only reported."""
        if found is not None:
            self.add(
                Kind.SKIPPED,
                project.name,
                found.path,
                f"in Orca, but {skip_reason()}",
                "remove it from Orca by hand, or drop it from the skip list",
            )

    def declared(self, project: Declared, found: Registration | None) -> None:
        group = project.group
        if group is None:
            if found is not None:
                self.add(
                    Kind.UNDECLARED,
                    project.name,
                    found.path,
                    "in Orca, but managed-projects.json gives it no orca.group",
                    'add "orca": {"group": ...} to its managed-projects.json entry',
                )
            return
        where = group or "the top level"
        if group and not self.groups.has(group) and group not in self.missing:
            self.missing.add(group)
            self.add(Kind.MISSING_GROUP, group, group, f"Orca has no group {group}", "create it", "estate orca --apply")
        if found is None:
            clone = expected_clone(project, self.groups, self.code_root)
            if self.exists(clone):
                self.add(
                    Kind.UNREGISTERED,
                    project.name,
                    clone,
                    "cloned, but not in Orca",
                    f"register it and move it to {where}",
                    "estate orca --apply",
                )
            else:
                self.add(
                    Kind.NO_CLONE,
                    project.name,
                    clone,
                    "neither in Orca nor cloned where its group expects",
                    "clone it, then run estate orca --apply",
                    f"git clone {project.repo} {clone.as_posix()}",
                )
            return
        if not self.exists(found.path):
            self.add(
                Kind.STALE,
                project.name,
                found.path,
                "registered in Orca, but the folder is gone",
                "remove it from Orca, or clone it back",
            )
            return
        actual = self.groups.path_of(found.group_id) or ""
        if actual != group:
            self.add(
                Kind.WRONG_GROUP,
                project.name,
                found.path,
                f"under {actual or 'no group'}, declared {where}",
                f"move it to {where}",
                "estate orca --apply",
            )


def _archived_clone(repo: str, path: pathlib.Path, slug: str) -> Finding:
    return Finding(
        Kind.ARCHIVED_CLONE.value,
        repo,
        path.as_posix(),
        f"{slug} is archived, but still cloned here",
        "delete the clone once nothing exists only there (estate risk shows what does)",
        "",
    )


def audit(
    declared: Iterable[Declared],
    state: Snapshot,
    *,
    code_root: pathlib.Path | None = None,
    exists: Callable[[pathlib.Path], bool] = pathlib.Path.is_dir,
    archived: frozenset[str] = frozenset(),
    skipped: frozenset[str] = frozenset(),
) -> list[Finding]:
    """Every difference between the declared layout and Orca, in managed-projects order.

    *archived* is the lowercase `owner/name` of each repo GitHub reports archived.
    *skipped* names the projects this machine never registers (`tools.estate.skip_repos`);
    they are reported only while registered.
    """
    projects = list(declared)
    by_slug = {r.slug: r for r in state.registrations if r.slug is not None}
    run = _Audit(state.groups, code_root or roots.code_root(), exists)
    known: set[str] = set()
    for project in projects:
        slug = project.slug.lower()
        known.add(slug)
        if project.name in skipped:
            run.skipped(project, by_slug.get(slug))
        elif project.archived or slug in archived:
            run.retired(project, by_slug.get(slug))
        else:
            run.declared(project, by_slug.get(slug))
    for slug, found in sorted(by_slug.items()):
        if slug not in known and slug in archived:
            run.add(
                Kind.ARCHIVED,
                found.name,
                found.path,
                f"in Orca, but {slug} is archived",
                "remove it from Orca",
                "estate orca --apply",
            )
            if exists(found.path):
                run.findings.append(_archived_clone(found.name, found.path, slug))
        elif slug not in known and scopes.is_estate_owner(slug.split("/", maxsplit=1)[0]):
            run.add(
                Kind.UNMANAGED,
                found.name,
                found.path,
                f"in Orca, but {slug} is not in managed-projects.json",
                "add it to managed-projects.json with an orca.group, or remove it from Orca",
            )
    for group in empty_groups(state, keep=_kept_paths(_unskipped(projects, skipped), archived)):
        path = state.groups.path_of(group.id) or group.name
        run.add(
            Kind.EMPTY_GROUP,
            path,
            path,
            "holds no project or folder workspace, and managed-projects.json declares no project in it",
            "delete it",
            "estate orca --apply",
        )
    return run.findings


def scoped(findings: Iterable[Finding], declared: Iterable[Declared], only: frozenset[str]) -> list[Finding]:
    """The findings about the projects named in *only*, and any group they are declared in that Orca lacks."""
    groups = {p.group for p in declared if p.name in only and p.group}
    return [f for f in findings if f.repo in only or (f.kind == Kind.MISSING_GROUP and f.repo in groups)]


# ---------------------------------------------------------------- applying it


def _ensure_group(runtime: OrcaRuntime, groups: Groups, path: str, log: Callable[[str], None]) -> Groups:
    """Create each missing segment of *path*, parents first; return the groups afterwards."""
    parent_id: str | None = None
    names = path.split("/")
    for depth in range(1, len(names) + 1):
        prefix = "/".join(names[:depth])
        existing = groups.id_of(prefix)
        if existing is None:
            created = runtime.create_group(names[depth - 1], parent_id)
            groups = Groups((*groups.rows, created))
            log(f"created group {prefix}")
            existing = created.id
        parent_id = existing
    return groups


def apply(
    runtime: OrcaRuntime,
    declared: Iterable[Declared],
    *,
    code_root: pathlib.Path | None = None,
    exists: Callable[[pathlib.Path], bool] = pathlib.Path.is_dir,
    log: Callable[[str], None] = lambda _line: None,
    archived: frozenset[str] = frozenset(),
    in_use: Callable[[pathlib.Path], bool | None] | None = None,
    skipped: frozenset[str] = frozenset(),
    only: frozenset[str] | None = None,
) -> list[Finding]:
    """Register, create groups, move projects and drop archived ones until Orca matches what can be matched.

    *only*, when given, names the managed projects to act on: only they are registered, removed
    or moved, only their groups are created, and no empty group is deleted, so the rest of the
    estate is left exactly as it was.

    Top-level projects move last, so each lands at the bottom of the top level.
    An archived project is removed from Orca (which only unregisters it; no file
    is touched), except while a process is working in it. Config fixes
    (`undeclared`, `unmanaged`), stale registrations, missing clones and archived
    clones are left for a human. A project in *skipped* is never registered, and its
    group is neither created nor kept for it; one already registered stays put.

    Returns the `archived-clone` findings for repos it unregistered that no entry
    lists: once out of Orca, nothing else would remember their clones.
    """
    busy = in_use or working_in
    carried: list[Finding] = []
    projects = list(declared)
    active = _unskipped(projects, skipped)
    state = snapshot(runtime)
    by_path = {r.path.as_posix(): r for r in state.registrations}
    for finding in audit(projects, state, code_root=code_root, exists=exists, archived=archived, skipped=skipped):
        if only is not None and finding.repo not in only:
            continue
        if finding.kind == Kind.UNREGISTERED:
            _ = runtime.add_repo(pathlib.Path(finding.path))
            log(f"registered {finding.repo}")
        elif finding.kind == Kind.ARCHIVED:
            found = by_path.get(finding.path)
            if found is None:
                continue
            activity = busy(found.path)
            if activity is None:
                log(f"left {finding.repo} in Orca: could not check whether a process is working in {finding.path}")
                continue
            if activity:
                log(f"left {finding.repo} in Orca: a process is working in {finding.path}")
                continue
            runtime.remove_repo(found.id)
            log(f"removed archived {finding.repo} from Orca")
            if found.slug not in {p.slug.lower() for p in projects} and exists(found.path):
                carried.append(_archived_clone(finding.repo, found.path, found.slug or finding.repo))

    scope = active if only is None else [p for p in active if p.name in only]
    state = snapshot(runtime)
    groups = state.groups
    for group in sorted(
        {p.group for p in scope if p.group and not p.archived and p.slug.lower() not in archived},
        key=lambda g: g.count("/"),
    ):
        groups = _ensure_group(runtime, groups, group, log)

    by_slug = {r.slug: r for r in state.registrations if r.slug is not None}
    live = [p for p in scope if not p.archived and p.slug.lower() not in archived]
    placed = [p for p in live if p.group is not None and p.slug.lower() in by_slug]
    for project in sorted(placed, key=lambda p: p.group == ""):
        found = by_slug[project.slug.lower()]
        target = project.group or ""
        if not exists(found.path) or (groups.path_of(found.group_id) or "") == target:
            continue
        _ = runtime.move_repo(found.id, groups.id_of(target) if target else None)
        log(f"moved {project.name} to {target or 'the top level'}")

    if only is not None:
        return carried
    state = snapshot(runtime)
    for group in empty_groups(state, keep=_kept_paths(active, archived)):
        if runtime.delete_group(group.id):
            log(f"deleted empty group {state.groups.path_of(group.id) or group.name}")
    return carried


# ---------------------------------------------------------------- the work profile


@dataclass(frozen=True, slots=True)
class _WorkPlan:
    evicted: list[Registration]
    emptied: list[Group]


def _work_plan(state: Snapshot) -> _WorkPlan:
    """The estate repos in the work profile, and the groups that only they kept occupied."""
    evicted = [r for r in state.registrations if r.slug and scopes.is_estate_owner(r.slug.split("/", maxsplit=1)[0])]
    touched = {group_id for r in evicted for group_id in state.groups.lineage(r.group_id)}
    return _WorkPlan(evicted, empty_groups(state, gone=(r.id for r in evicted), within=touched))


def audit_work(state: Snapshot) -> list[Finding]:
    """Estate repos in the work Orca profile, and the groups that would be left empty without them."""
    plan = _work_plan(state)
    findings = [
        Finding(
            Kind.IN_WORK_PROFILE.value,
            r.name,
            r.path.as_posix(),
            f"{r.slug} belongs to the personal estate, but it is in the work Orca profile",
            "remove it from the work profile",
            "estate orca --apply",
        )
        for r in plan.evicted
    ]
    for group in plan.emptied:
        path = state.groups.path_of(group.id) or group.name
        findings.append(
            Finding(
                Kind.WORK_EMPTY_GROUP.value,
                path,
                path,
                "a work-profile group that holds only estate repos",
                "delete it once they are removed",
                "estate orca --apply",
            )
        )
    return findings


def apply_work(runtime: OrcaRuntime, *, log: Callable[[str], None] = lambda _line: None) -> None:
    """Take the estate repos out of the work profile, then delete the groups they leave empty.

    Removal only unregisters a repo and stops its terminals in that profile; no file is touched.
    """
    state = snapshot(runtime)
    plan = _work_plan(state)
    for found in plan.evicted:
        runtime.remove_repo(found.id)
        log(f"removed {found.name} from the work Orca profile")
    for group in plan.emptied:
        if runtime.delete_group(group.id):
            log(f"deleted work-profile group {state.groups.path_of(group.id) or group.name}")


def working_in(path: pathlib.Path) -> bool | None:
    """Whether any process's working directory is *path* or below it; None when that cannot be checked.

    None (no `lsof`, Windows, a failure or timeout) is not "idle": removal stops the
    project's terminals, so the caller must treat an unchecked project as in use.
    """
    dirs = busy.working_dirs()
    return None if dirs is None else busy.in_use(path, dirs)
