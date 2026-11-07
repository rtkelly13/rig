from __future__ import annotations

import json
import pathlib
import shutil
import sys
from collections.abc import Iterator
from dataclasses import dataclass, field
from typing import Annotated

import pytest
from pydantic import Field, TypeAdapter
from typer.testing import CliRunner

from rig.estate import orca, orca_cli
from rig.estate.cli import app
from rig.estate.orca import Declared, Kind
from rig.estate.orca_runtime import ORCA_UNAVAILABLE_EXIT, OrcaRuntime, OrcaUnavailableError

pytestmark = pytest.mark.ci

TOKEN = "secret-token"
type Json = dict[str, object]


@dataclass(frozen=True)
class Request:
    id: str
    auth_token: Annotated[str, Field(alias="authToken")]
    method: str
    params: dict[str, str | None] | None


REQUEST = TypeAdapter(Request)


@dataclass
class FakeOrca:
    """Orca's runtime on the wire: newline-delimited JSON, keepalives first, answers by id."""

    repos: list[Json] = field(default_factory=list[Json])
    groups: list[Json] = field(default_factory=list[Json])
    folder_workspaces: list[Json] = field(default_factory=list[Json])
    calls: list[tuple[str, Json | None]] = field(default_factory=list[tuple[str, Json | None]])

    def runtime(self) -> OrcaRuntime:
        return OrcaRuntime("/tmp/orca.sock", TOKEN, self.exchange)

    def exchange(self, endpoint: str, request: bytes) -> Iterator[bytes]:
        assert endpoint == "/tmp/orca.sock"
        assert request.endswith(b"\n")
        body = REQUEST.validate_json(request)
        assert body.auth_token == TOKEN
        params: Json | None = dict(body.params) if body.params is not None else None
        self.calls.append((body.method, params))
        yield b'{"_keepalive": true}\n'
        yield (json.dumps({"id": "someone-else", "ok": True, "result": {}}) + "\n").encode()
        yield (
            json.dumps({"id": body.id, "ok": True, "result": self.answer(body.method, params or {})}) + "\n"
        ).encode()

    def answer(self, method: str, params: Json) -> Json:
        if method == "repo.list":
            return {"repos": self.repos}
        if method == "projectGroup.list":
            return {"groups": self.groups}
        if method == "folderWorkspace.list":
            return {"folderWorkspaces": self.folder_workspaces}
        if method == "projectGroup.delete":
            return {"deleted": self.delete_group(str(params["groupId"]))}
        if method == "repo.add":
            path = str(params["path"])
            name = pathlib.Path(path).name
            repo: Json = {"id": f"r-{name}", "path": path, "displayName": name, "projectGroupId": None}
            repo["gitRemoteIdentity"] = {"canonicalKey": f"github.com/rtkelly-labs/{name}"}
            self.repos.append(repo)
            return {"repo": repo}
        if method == "projectGroup.create":
            group: Json = {
                "id": f"g-{params['name']}",
                "name": params["name"],
                "parentGroupId": params["parentGroupId"],
            }
            self.groups.append(group)
            return {"group": group}
        if method == "repo.rm":
            repo_id = str(params["repo"]).removeprefix("id:")
            self.repos[:] = [r for r in self.repos if r["id"] != repo_id]
            return {"removed": True}
        if method == "projectGroup.moveProject":
            repo_id = str(params["repo"]).removeprefix("id:")
            moved = next(r for r in self.repos if r["id"] == repo_id)
            moved["projectGroupId"] = params["groupId"]
            return {"repo": moved}
        raise AssertionError(method)

    def delete_group(self, group_id: str) -> bool:
        """As Orca does: the group and its subgroups go, their projects move to the top level."""
        doomed = {group_id}
        while grown := {str(g["id"]) for g in self.groups if g.get("parentGroupId") in doomed} - doomed:
            doomed |= grown
        before = len(self.groups)
        self.groups[:] = [g for g in self.groups if g["id"] not in doomed]
        for repo in self.repos:
            if repo["projectGroupId"] in doomed:
                repo["projectGroupId"] = None
        return len(self.groups) < before


def _repo(code: pathlib.Path, clone: str, owner: str, group_id: str | None) -> Json:
    name = pathlib.Path(clone).name
    return {
        "id": f"r-{name}",
        "path": (code / clone).as_posix(),
        "displayName": name,
        "projectGroupId": group_id,
        "gitRemoteIdentity": {"canonicalKey": f"github.com/{owner}/{name}"},
        "badgeColor": "#737373",
    }


@pytest.fixture(autouse=True)
def no_archived_repos(monkeypatch: pytest.MonkeyPatch) -> None:
    """Keep GitHub out of the CLI tests: the fixture repo names are real, and one of them is archived."""

    def none_archived(_slugs: object) -> frozenset[str]:
        return frozenset()

    monkeypatch.setattr(orca_cli, "archived_slugs", none_archived)


@pytest.fixture
def code(tmp_path: pathlib.Path) -> pathlib.Path:
    root = tmp_path / "code"
    for clone in ("personal/rig", "personal/blobflags", "rtkelly-labs/Iceberg.NET", "rtkelly-labs/Diffra"):
        (root / clone).mkdir(parents=True)
    return root


@pytest.fixture
def fake(code: pathlib.Path) -> FakeOrca:
    return FakeOrca(
        repos=[
            _repo(code, "personal/rig", "rtkelly13", "g-tooling"),
            _repo(code, "personal/blobflags", "rtkelly13", "g-tooling"),
            _repo(code, "rtkelly-labs/Iceberg.NET", "rtkelly-labs", None),
        ],
        groups=[
            {
                "id": "g-personal",
                "name": "personal",
                "parentGroupId": None,
                "parentPath": (code / "personal").as_posix(),
            },
            {"id": "g-tooling", "name": "tooling", "parentGroupId": "g-personal"},
            {
                "id": "g-labs",
                "name": "rtkelly-labs",
                "parentGroupId": None,
                "parentPath": (code / "rtkelly-labs").as_posix(),
            },
        ],
    )


def _declared(name: str, owner: str, group: str | None) -> Declared:
    return Declared(name, f"git@github.com:{owner}/{name}.git", f"{owner}/{name}", group)


LAYOUT = [
    _declared("rig", "rtkelly13", ""),
    _declared("blobflags", "rtkelly13", "personal/tooling"),
    _declared("Iceberg.NET", "rtkelly-labs", "rtkelly-labs"),
    _declared("Diffra", "rtkelly-labs", "rtkelly-labs"),
]


def _kinds(findings: list[orca.Finding]) -> list[tuple[str, str]]:
    return [(f.kind, f.repo) for f in findings]


# ---------------------------------------------------------------- the runtime client


def test_connect_reads_the_transport_and_token_orca_writes(tmp_path: pathlib.Path) -> None:
    metadata = {
        "runtimeId": "x",
        "authToken": TOKEN,
        "transports": [{"kind": "websocket", "endpoint": "ws://x"}, {"kind": "unix", "endpoint": "/tmp/o.sock"}],
    }
    _ = (tmp_path / "orca-runtime.json").write_text(json.dumps(metadata), encoding="utf-8")

    runtime = OrcaRuntime.connect(tmp_path)

    assert (runtime.endpoint, runtime.token) == ("/tmp/o.sock", TOKEN)


def test_connect_accepts_the_legacy_single_transport(tmp_path: pathlib.Path) -> None:
    metadata = {"authToken": TOKEN, "transport": {"kind": "named-pipe", "endpoint": r"\\.\pipe\orca"}}
    _ = (tmp_path / "orca-runtime.json").write_text(json.dumps(metadata), encoding="utf-8")

    assert OrcaRuntime.connect(tmp_path).endpoint == r"\\.\pipe\orca"


@pytest.mark.parametrize("content", [None, "{}", '{"authToken": "t", "transports": []}', "not json"])
def test_connect_refuses_when_orca_is_not_running_or_wrote_nothing_usable(
    tmp_path: pathlib.Path, content: str | None
) -> None:
    if content is not None:
        _ = (tmp_path / "orca-runtime.json").write_text(content, encoding="utf-8")

    with pytest.raises(OrcaUnavailableError):
        _ = OrcaRuntime.connect(tmp_path)


def test_calls_skip_keepalives_and_other_answers_and_decode_their_result(fake: FakeOrca) -> None:
    repos = fake.runtime().repos()

    assert [(r.display_name, r.group_id) for r in repos] == [
        ("rig", "g-tooling"),
        ("blobflags", "g-tooling"),
        ("Iceberg.NET", None),
    ]
    assert fake.calls == [("repo.list", None)]


def test_a_refusal_raises_with_orcas_code_and_message() -> None:
    def refuse(_endpoint: str, request: bytes) -> Iterator[bytes]:
        request_id = REQUEST.validate_json(request).id
        yield (
            json.dumps({"id": request_id, "ok": False, "error": {"code": "not_found", "message": "no repo"}}) + "\n"
        ).encode()

    with pytest.raises(OrcaUnavailableError, match="not_found no repo"):
        _ = OrcaRuntime("e", TOKEN, refuse).repos()


def test_a_connection_that_closes_without_an_answer_raises() -> None:
    def hang_up(_endpoint: str, _request: bytes) -> Iterator[bytes]:
        yield b'{"_keepalive": true}\n'

    with pytest.raises(OrcaUnavailableError, match="closed the connection"):
        _ = OrcaRuntime("e", TOKEN, hang_up).groups()


# ---------------------------------------------------------------- reading the layout


def test_load_declared_reads_groups_top_level_and_skips_what_orca_cannot_hold(tmp_path: pathlib.Path) -> None:
    config = tmp_path / "managed-projects.json"
    projects = [
        {"name": "rig", "repo": "git@github.com:rtkelly13/rig.git", "orca": {"group": ""}},
        {
            "name": "blobflags",
            "repo": "git@github.com:rtkelly13/blobflags.git",
            "orca": {"group": "/personal/tooling/"},
        },
        {"name": "reference", "repo": "git@github.com:someone/reference.git"},
        {"name": "old", "repo": "git@github.com:rtkelly13/old.git", "enabled": False, "orca": {"group": "personal"}},
        {"name": "backup", "repo": "local/backup", "orca": {"group": "personal"}},
    ]
    _ = config.write_text(json.dumps({"projects": projects}), encoding="utf-8")

    declared = orca.load_declared(config)

    assert [(d.name, d.slug, d.group) for d in declared] == [
        ("rig", "rtkelly13/rig", ""),
        ("blobflags", "rtkelly13/blobflags", "personal/tooling"),
        ("reference", "someone/reference", None),
    ]


def test_groups_resolve_nested_paths_and_their_scanned_folders(fake: FakeOrca, code: pathlib.Path) -> None:
    groups = orca.snapshot(fake.runtime()).groups

    assert groups.path_of("g-tooling") == "personal/tooling"
    assert groups.path_of(None) is None
    assert groups.path_of("gone") is None
    assert groups.id_of("rtkelly-labs") == "g-labs"
    assert not groups.has("personal/apps")
    assert groups.folder("rtkelly-labs") == code / "rtkelly-labs"
    assert groups.folder("tooling") is None


# ---------------------------------------------------------------- the audit


def test_the_audit_names_every_difference_and_what_closes_it(fake: FakeOrca, code: pathlib.Path) -> None:
    fake.repos.append(_repo(code, "personal/ops", "rtkelly13", "g-personal"))
    fake.repos.append(_repo(code, "acme/platform", "acme-corp", None))
    declared = [
        *LAYOUT,
        _declared("Parquet", "rtkelly-labs", "rtkelly-labs"),
        _declared("apps", "rtkelly13", "personal/apps"),
    ]

    findings = orca.audit(declared, orca.snapshot(fake.runtime()), code_root=code)

    assert _kinds(findings) == [
        (Kind.WRONG_GROUP, "rig"),
        (Kind.WRONG_GROUP, "Iceberg.NET"),
        (Kind.UNREGISTERED, "Diffra"),
        (Kind.NO_CLONE, "Parquet"),
        (Kind.MISSING_GROUP, "personal/apps"),
        (Kind.NO_CLONE, "apps"),
        (Kind.UNMANAGED, "ops"),
    ]
    assert findings[0].reason == "under personal/tooling, declared the top level"
    assert findings[1].reason == "under no group, declared rtkelly-labs"
    labs = (code / "rtkelly-labs").as_posix()
    assert findings[2].path == f"{labs}/Diffra"
    assert findings[3].command == f"git clone git@github.com:rtkelly-labs/Parquet.git {labs}/Parquet"


def test_an_entry_without_a_placement_is_undeclared_only_when_orca_has_it(fake: FakeOrca, code: pathlib.Path) -> None:
    declared = [_declared("blobflags", "rtkelly13", None), _declared("reference", "someone", None)]

    findings = orca.audit(declared, orca.snapshot(fake.runtime()), code_root=code)

    assert (Kind.UNDECLARED, "blobflags") in _kinds(findings)
    assert all(f.repo != "reference" for f in findings)


def test_a_registration_whose_folder_is_gone_is_stale(fake: FakeOrca, code: pathlib.Path) -> None:
    fake.repos.append(_repo(code, "personal/gone", "rtkelly13", "g-tooling"))
    declared = [*LAYOUT, _declared("gone", "rtkelly13", "personal/tooling")]

    findings = orca.audit(declared, orca.snapshot(fake.runtime()), code_root=code)

    assert [(f.kind, f.reason) for f in findings if f.repo == "gone"] == [
        (Kind.STALE, "registered in Orca, but the folder is gone")
    ]


# ---------------------------------------------------------------- applying it


def test_apply_registers_creates_groups_and_moves_with_top_level_projects_last(
    fake: FakeOrca, code: pathlib.Path
) -> None:
    (code / "personal" / "new").mkdir()
    declared = [*LAYOUT, _declared("new", "rtkelly-labs", "personal/apps/games")]
    fake.repos.append(_repo(code, "personal/new", "rtkelly-labs", None))
    log: list[str] = []

    orca.apply(fake.runtime(), declared, code_root=code, log=log.append)

    assert log == [
        "registered Diffra",
        "created group personal/apps",
        "created group personal/apps/games",
        "moved Iceberg.NET to rtkelly-labs",
        "moved Diffra to rtkelly-labs",
        "moved new to personal/apps/games",
        "moved rig to the top level",
    ]
    assert orca.audit(declared, orca.snapshot(fake.runtime()), code_root=code) == []
    creates = [params for method, params in fake.calls if method == "projectGroup.create"]
    assert creates == [{"name": "apps", "parentGroupId": "g-personal"}, {"name": "games", "parentGroupId": "g-apps"}]
    assert ("projectGroup.moveProject", {"repo": "id:r-rig", "groupId": None}) in fake.calls


def test_apply_changes_nothing_when_orca_already_matches(fake: FakeOrca, code: pathlib.Path) -> None:
    orca.apply(fake.runtime(), LAYOUT, code_root=code)
    before = len(fake.calls)

    orca.apply(fake.runtime(), LAYOUT, code_root=code)

    assert {method for method, _ in fake.calls[before:]} == {"repo.list", "projectGroup.list", "folderWorkspace.list"}


def test_apply_with_only_registers_and_groups_the_named_projects_and_leaves_the_rest(
    fake: FakeOrca, code: pathlib.Path
) -> None:
    fake.groups.append({"id": "g-empty", "name": "empty", "parentGroupId": None})
    log: list[str] = []

    orca.apply(fake.runtime(), LAYOUT, code_root=code, log=log.append, only=frozenset({"Diffra"}))

    assert log == ["registered Diffra", "moved Diffra to rtkelly-labs"]
    findings = orca.audit(LAYOUT, orca.snapshot(fake.runtime()), code_root=code)
    assert _kinds(findings) == [
        (Kind.WRONG_GROUP, "rig"),
        (Kind.WRONG_GROUP, "Iceberg.NET"),
        (Kind.EMPTY_GROUP, "empty"),
    ]


# ---------------------------------------------------------------- the command


def test_the_command_applies_then_reports_what_is_left(
    tmp_path: pathlib.Path, code: pathlib.Path, fake: FakeOrca, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = tmp_path / "managed-projects.json"
    entries = [{"name": d.name, "repo": d.repo, "orca": {"group": d.group}} for d in LAYOUT]
    entries.append({"name": "blobflags-docs", "repo": "git@github.com:rtkelly13/blobflags-docs.git"})
    _ = config.write_text(json.dumps({"projects": entries}), encoding="utf-8")
    fake.repos.append(_repo(code, "personal/ops", "rtkelly13", None))
    work = FakeOrca(
        repos=[_repo(code, "rtkelly-labs/Diffra", "rtkelly-labs", "g-labs")],
        groups=[{"id": "g-labs", "name": "rtkelly-labs", "parentGroupId": None}],
    )
    profiles = {tmp_path / "estate": fake, tmp_path / "work": work}

    def connect(user_data: pathlib.Path | None = None) -> OrcaRuntime:
        assert user_data is not None
        return profiles[user_data].runtime()

    monkeypatch.setattr(OrcaRuntime, "connect", staticmethod(connect))
    flags = ["--orca-data", str(tmp_path / "estate"), "--work-orca-data", str(tmp_path / "work")]

    result = CliRunner().invoke(
        app, ["orca", "--config", str(config), "--code-root", str(code), *flags, "--apply", "--json"]
    )

    assert result.exit_code == 0, result.output
    findings = TypeAdapter(list[orca.Finding]).validate_json(result.stdout)
    assert _kinds(findings) == [(Kind.UNMANAGED, "ops")]
    assert work.repos == []
    assert work.groups == []


def test_the_command_reports_the_work_profile_and_carries_on_when_it_is_not_running(
    tmp_path: pathlib.Path, code: pathlib.Path, fake: FakeOrca, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = tmp_path / "managed-projects.json"
    entries = [{"name": d.name, "repo": d.repo, "orca": {"group": d.group}} for d in LAYOUT]
    _ = config.write_text(json.dumps({"projects": entries}), encoding="utf-8")
    orca.apply(fake.runtime(), LAYOUT, code_root=code)

    def connect(user_data: pathlib.Path | None = None) -> OrcaRuntime:
        if user_data == tmp_path / "work":
            raise OrcaUnavailableError("not running")
        return fake.runtime()

    monkeypatch.setattr(OrcaRuntime, "connect", staticmethod(connect))
    flags = ["--orca-data", str(tmp_path / "estate"), "--work-orca-data", str(tmp_path / "work")]

    result = CliRunner().invoke(app, ["orca", "--config", str(config), "--code-root", str(code), *flags, "--json"])

    assert result.exit_code == 0, result.output
    assert TypeAdapter(list[orca.Finding]).validate_json(result.stdout) == []
    assert "work Orca profile not checked" in result.stderr


def test_the_command_never_evicts_the_estate_when_both_profiles_are_one_folder(
    tmp_path: pathlib.Path, code: pathlib.Path, fake: FakeOrca, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = tmp_path / "managed-projects.json"
    entries = [{"name": d.name, "repo": d.repo, "orca": {"group": d.group}} for d in LAYOUT]
    _ = config.write_text(json.dumps({"projects": entries}), encoding="utf-8")
    (tmp_path / "estate").mkdir()
    connected: list[pathlib.Path | None] = []

    def connect(user_data: pathlib.Path | None = None) -> OrcaRuntime:
        connected.append(user_data)
        return fake.runtime()

    monkeypatch.setattr(OrcaRuntime, "connect", staticmethod(connect))
    same = str(tmp_path / "estate" / ".." / "estate")
    flags = ["--orca-data", str(tmp_path / "estate"), "--work-orca-data", same]

    result = CliRunner().invoke(
        app, ["orca", "--config", str(config), "--code-root", str(code), *flags, "--apply", "--json"]
    )

    assert result.exit_code == 0, result.output
    assert connected == [tmp_path / "estate"]
    assert len(fake.repos) == len(LAYOUT)


def test_the_command_with_repo_applies_and_reports_only_the_named_project(
    tmp_path: pathlib.Path, code: pathlib.Path, fake: FakeOrca, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = tmp_path / "managed-projects.json"
    entries = [{"name": d.name, "repo": d.repo, "orca": {"group": d.group}} for d in LAYOUT]
    _ = config.write_text(json.dumps({"projects": entries}), encoding="utf-8")
    work = FakeOrca(
        repos=[_repo(code, "rtkelly-labs/Diffra", "rtkelly-labs", "g-labs")],
        groups=[{"id": "g-labs", "name": "rtkelly-labs", "parentGroupId": None}],
    )
    profiles = {tmp_path / "estate": fake, tmp_path / "work": work}

    def connect(user_data: pathlib.Path | None = None) -> OrcaRuntime:
        assert user_data is not None
        return profiles[user_data].runtime()

    monkeypatch.setattr(OrcaRuntime, "connect", staticmethod(connect))
    flags = ["--orca-data", str(tmp_path / "estate"), "--work-orca-data", str(tmp_path / "work")]
    base = ["orca", "--config", str(config), "--code-root", str(code), *flags]

    result = CliRunner().invoke(app, [*base, "--apply", "--repo", "Diffra", "--json"])

    assert result.exit_code == 0, result.output
    assert TypeAdapter(list[orca.Finding]).validate_json(result.stdout) == []
    diffra = next(r for r in fake.repos if r["displayName"] == "Diffra")
    iceberg = next(r for r in fake.repos if r["displayName"] == "Iceberg.NET")
    assert (diffra["projectGroupId"], iceberg["projectGroupId"]) == ("g-labs", None)
    assert len(work.repos) == 1
    assert CliRunner().invoke(app, [*base, "--repo", "Difra"]).exit_code == 2


def test_the_command_exits_3_when_orca_is_not_running(tmp_path: pathlib.Path) -> None:
    config = tmp_path / "managed-projects.json"
    _ = config.write_text('{"projects": []}', encoding="utf-8")

    result = CliRunner().invoke(app, ["orca", "--config", str(config), "--orca-data", str(tmp_path / "no-orca")])

    assert result.exit_code == ORCA_UNAVAILABLE_EXIT


# ---------------------------------------------------------------- archived repos


ICEBERG_ARCHIVED = frozenset({"rtkelly-labs/iceberg.net"})


def test_an_archived_repo_is_never_placed_only_flagged_for_removal(code: pathlib.Path, fake: FakeOrca) -> None:
    archived = frozenset({"rtkelly-labs/iceberg.net", "rtkelly-labs/diffra"})
    findings = orca.audit(LAYOUT, orca.snapshot(fake.runtime()), code_root=code, archived=archived)
    kinds = _kinds(findings)
    assert (Kind.ARCHIVED.value, "Iceberg.NET") in kinds
    assert (Kind.ARCHIVED_CLONE.value, "Iceberg.NET") in kinds
    assert (Kind.ARCHIVED_CLONE.value, "Diffra") in kinds
    assert not [k for k in kinds if k[1] in {"Iceberg.NET", "Diffra"} and k[0] not in {"archived", "archived-clone"}], (
        "an archived repo is never asked to be registered, moved or cloned"
    )


def test_apply_removes_an_archived_registration_but_never_touches_its_clone(code: pathlib.Path, fake: FakeOrca) -> None:
    orca.apply(fake.runtime(), LAYOUT, code_root=code, archived=ICEBERG_ARCHIVED, in_use=lambda _path: False)
    assert ("repo.rm", {"repo": "id:r-Iceberg.NET"}) in fake.calls
    assert not any(r["id"] == "r-Iceberg.NET" for r in fake.repos)
    assert (code / "rtkelly-labs" / "Iceberg.NET").is_dir()


def test_apply_leaves_an_archived_repo_a_process_is_working_in(code: pathlib.Path, fake: FakeOrca) -> None:
    lines: list[str] = []
    orca.apply(
        fake.runtime(), LAYOUT, code_root=code, archived=ICEBERG_ARCHIVED, in_use=lambda _path: True, log=lines.append
    )
    assert not any(method == "repo.rm" for method, _ in fake.calls)
    assert any("a process is working in" in line for line in lines)


def test_archived_in_the_registry_counts_without_asking_github(code: pathlib.Path, fake: FakeOrca) -> None:
    layout = [*LAYOUT[:2], Declared("Iceberg.NET", "x", "rtkelly-labs/Iceberg.NET", "rtkelly-labs", archived=True)]
    findings = orca.audit(layout, orca.snapshot(fake.runtime()), code_root=code)
    assert (Kind.ARCHIVED.value, "Iceberg.NET") in _kinds(findings)


def test_an_unlisted_archived_estate_repo_is_archived_not_unmanaged(code: pathlib.Path, fake: FakeOrca) -> None:
    findings = orca.audit(LAYOUT[:2], orca.snapshot(fake.runtime()), code_root=code, archived=ICEBERG_ARCHIVED)
    assert (Kind.ARCHIVED.value, "Iceberg.NET") in _kinds(findings)
    assert (Kind.UNMANAGED.value, "Iceberg.NET") not in _kinds(findings)


def test_load_declared_reads_archived_projects_from_their_own_file(tmp_path: pathlib.Path) -> None:
    # Arrange
    config = tmp_path / "managed-projects.json"
    live = [
        {"name": "live", "repo": "git@github.com:rtkelly13/live.git"},
        {"name": "gone", "repo": "git@github.com:rtkelly13/gone.git", "enabled": False},
    ]
    _ = config.write_text(json.dumps({"projects": live}), encoding="utf-8")
    old = [{"name": "old", "repo": "git@github.com:rtkelly13/old.git", "reason": "archived"}]
    _ = (tmp_path / orca.ARCHIVED_PROJECTS).write_text(json.dumps({"projects": old}), encoding="utf-8")

    # Act
    declared = orca.load_declared(config)

    # Assert
    assert [(d.name, d.archived) for d in declared] == [("live", False), ("old", True)], (
        "archived projects come only from archived-projects.json; disabled managed entries are dropped"
    )


# ---------------------------------------------------------------- review findings on #349


def test_apply_leaves_an_archived_repo_when_activity_cannot_be_checked(code: pathlib.Path, fake: FakeOrca) -> None:
    """Removal stops the project's terminals, so "could not check" must not count as idle."""
    lines: list[str] = []
    _ = orca.apply(
        fake.runtime(), LAYOUT, code_root=code, archived=ICEBERG_ARCHIVED, in_use=lambda _path: None, log=lines.append
    )
    assert not any(method == "repo.rm" for method, _ in fake.calls)
    assert any("could not check" in line for line in lines)


def test_working_in_cannot_tell_without_lsof(monkeypatch: pytest.MonkeyPatch, tmp_path: pathlib.Path) -> None:
    def no_lsof(name: str) -> str | None:
        assert name == "lsof"
        return None

    monkeypatch.setattr(shutil, "which", no_lsof)
    assert orca.working_in(tmp_path) is None


def test_an_unlisted_archived_repos_clone_is_still_reported_after_removal(code: pathlib.Path, fake: FakeOrca) -> None:
    layout = LAYOUT[:2]  # Iceberg.NET is registered and cloned, but listed nowhere
    before = orca.audit(layout, orca.snapshot(fake.runtime()), code_root=code, archived=ICEBERG_ARCHIVED)
    assert (Kind.ARCHIVED_CLONE.value, "Iceberg.NET") in _kinds(before)

    carried = orca.apply(fake.runtime(), layout, code_root=code, archived=ICEBERG_ARCHIVED, in_use=lambda _path: False)
    after = orca.audit(layout, orca.snapshot(fake.runtime()), code_root=code, archived=ICEBERG_ARCHIVED)
    assert (Kind.ARCHIVED_CLONE.value, "Iceberg.NET") not in _kinds(after), "nothing left in Orca to find it by"
    assert _kinds(carried) == [(Kind.ARCHIVED_CLONE.value, "Iceberg.NET")]


# ---------------------------------------------------------------- empty groups


def _group(group_id: str, name: str, parent: str | None) -> Json:
    return {"id": group_id, "name": name, "parentGroupId": parent}


def test_an_undeclared_group_with_nothing_in_it_is_reported_once_at_its_outermost(
    fake: FakeOrca, code: pathlib.Path
) -> None:
    fake.groups += [_group("g-old", "old", "g-personal"), _group("g-older", "older", "g-old")]

    findings = orca.audit(LAYOUT, orca.snapshot(fake.runtime()), code_root=code)

    assert [(f.kind, f.repo) for f in findings if f.kind == Kind.EMPTY_GROUP] == [(Kind.EMPTY_GROUP, "personal/old")]


def test_a_declared_group_a_folder_workspace_group_and_an_occupied_group_are_kept(
    fake: FakeOrca, code: pathlib.Path
) -> None:
    fake.groups += [
        _group("g-apps", "apps", "g-personal"),
        _group("g-notes", "notes", None),
        _group("g-hand", "hand", None),
    ]
    fake.folder_workspaces.append({"id": "w-1", "projectGroupId": "g-notes", "name": "notes"})
    fake.repos.append(_repo(code, "acme/platform", "acme-corp", "g-hand"))
    declared = [*LAYOUT, _declared("apps", "rtkelly13", "personal/apps")]

    findings = orca.audit(declared, orca.snapshot(fake.runtime()), code_root=code)

    assert all(f.kind != Kind.EMPTY_GROUP for f in findings)


def test_apply_deletes_empty_groups_including_ones_it_just_emptied(fake: FakeOrca, code: pathlib.Path) -> None:
    fake.groups += [_group("g-old", "old", None)]
    fake.repos[2]["projectGroupId"] = "g-old"
    log: list[str] = []

    orca.apply(fake.runtime(), LAYOUT, code_root=code, log=log.append)

    assert log[-1] == "deleted empty group old"
    assert ("projectGroup.delete", {"groupId": "g-old"}) in fake.calls
    assert orca.audit(LAYOUT, orca.snapshot(fake.runtime()), code_root=code) == []


# ---------------------------------------------------------------- the work profile


@pytest.fixture
def work(code: pathlib.Path) -> FakeOrca:
    return FakeOrca(
        repos=[
            _repo(code, "rtkelly-labs/Diffra", "rtkelly-labs", "g-labs-deep"),
            _repo(code, "personal/rig", "rtkelly13", None),
            _repo(code, "acme/platform", "acme-corp", "g-acme"),
            _repo(code, "acme/data", "acme-corp", "g-mixed"),
            _repo(code, "personal/blobflags", "rtkelly13", "g-mixed"),
        ],
        groups=[
            _group("g-labs", "rtkelly-labs", None),
            _group("g-labs-deep", "parquet", "g-labs"),
            _group("g-acme", "acme", None),
            _group("g-mixed", "mixed", None),
            _group("g-spare", "spare", None),
        ],
    )


def test_the_work_audit_names_estate_repos_and_the_groups_only_they_occupy(work: FakeOrca) -> None:
    findings = orca.audit_work(orca.snapshot(work.runtime()))

    assert _kinds(findings) == [
        (Kind.IN_WORK_PROFILE, "Diffra"),
        (Kind.IN_WORK_PROFILE, "rig"),
        (Kind.IN_WORK_PROFILE, "blobflags"),
        (Kind.WORK_EMPTY_GROUP, "rtkelly-labs"),
    ]


def test_apply_work_removes_estate_repos_and_their_groups_and_leaves_work_alone(work: FakeOrca) -> None:
    log: list[str] = []

    orca.apply_work(work.runtime(), log=log.append)

    assert [str(r["displayName"]) for r in work.repos] == ["platform", "data"]
    assert [str(g["id"]) for g in work.groups] == ["g-acme", "g-mixed", "g-spare"]
    assert log[-1] == "deleted work-profile group rtkelly-labs"
    assert orca.audit_work(orca.snapshot(work.runtime())) == []


# ---------------------------------------------------------------- which profile


def test_the_estate_has_its_own_profile_on_windows_and_shares_orcas_elsewhere(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    from rig.estate import orca_runtime

    monkeypatch.setattr(sys, "platform", "win32")
    assert orca_runtime.estate_user_data().name == "orca-personal"
    assert orca_runtime.work_user_data() == orca_runtime.default_user_data()

    monkeypatch.setattr(sys, "platform", "darwin")
    assert orca_runtime.estate_user_data() == orca_runtime.default_user_data()
    assert orca_runtime.work_user_data() is None


# ---------------------------------------------------------------- repos this machine skips


def test_a_skipped_repo_is_never_unregistered_or_missing_and_is_reported_only_while_registered(
    code: pathlib.Path, fake: FakeOrca
) -> None:
    declared = [*LAYOUT, _declared("hearth", "rtkelly13", "personal/home")]
    skipped = frozenset({"Iceberg.NET", "hearth"})

    findings = orca.audit(declared, orca.snapshot(fake.runtime()), code_root=code, skipped=skipped)

    assert _kinds(findings) == [(Kind.WRONG_GROUP, "rig"), (Kind.SKIPPED, "Iceberg.NET"), (Kind.UNREGISTERED, "Diffra")]
    assert findings[1].reason == "in Orca, but listed in tools.estate.skip_repos on this machine"


def test_apply_never_registers_moves_or_removes_a_skipped_repo_nor_creates_its_group(
    code: pathlib.Path, fake: FakeOrca
) -> None:
    declared = [*LAYOUT, _declared("hearth", "rtkelly13", "personal/home")]
    (code / "personal" / "hearth").mkdir()
    log: list[str] = []

    orca.apply(
        fake.runtime(),
        declared,
        code_root=code,
        log=log.append,
        skipped=frozenset({"Iceberg.NET", "hearth"}),
    )

    assert log == ["registered Diffra", "moved Diffra to rtkelly-labs", "moved rig to the top level"]
    assert {r["id"] for r in fake.repos} == {"r-rig", "r-blobflags", "r-Iceberg.NET", "r-Diffra"}
    assert next(r for r in fake.repos if r["id"] == "r-Iceberg.NET")["projectGroupId"] is None


def test_the_command_reads_the_skip_list_from_the_workspace_config(
    tmp_path: pathlib.Path, code: pathlib.Path, fake: FakeOrca, monkeypatch: pytest.MonkeyPatch
) -> None:
    config = tmp_path / "managed-projects.json"
    entries = [{"name": d.name, "repo": d.repo, "orca": {"group": d.group}} for d in LAYOUT]
    _ = config.write_text(json.dumps({"projects": entries}), encoding="utf-8")
    home = tmp_path / "workspace-home"
    home.mkdir()
    skip = {"tools": {"estate": {"skip_repos": ["Diffra"]}}}
    _ = (home / "config.json").write_text(json.dumps(skip), encoding="utf-8")
    monkeypatch.setenv("WORKSPACE_HOME", str(home))
    monkeypatch.setattr(OrcaRuntime, "connect", staticmethod(lambda _user_data=None: fake.runtime()))
    estate = str(tmp_path / "estate")

    result = CliRunner().invoke(
        app,
        ["orca", "--config", str(config), "--code-root", str(code), "--orca-data", estate, "--work-orca-data", estate]
        + ["--apply", "--json"],
    )

    assert result.exit_code == 0, result.output
    assert TypeAdapter(list[orca.Finding]).validate_json(result.stdout) == []
    assert not any(method == "repo.add" for method, _ in fake.calls)
