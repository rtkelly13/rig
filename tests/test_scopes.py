from __future__ import annotations

import os
from pathlib import Path

import pytest
from pydantic import ValidationError

from rig import roots, scopes

pytestmark = pytest.mark.ci


def _scopes_file(tmp_path: Path, text: str, monkeypatch: pytest.MonkeyPatch) -> None:
    config = tmp_path / "estate-scopes.json"
    _ = config.write_text(text, encoding="utf-8")
    monkeypatch.setattr(roots, "checkout_config", lambda name: tmp_path / name)


def _machine(work_dirs: list[str]) -> None:
    entries = ", ".join(f'"{entry}"' for entry in work_dirs).replace("\\", "\\\\")
    config = Path(os.environ["WORKSPACE_HOME"]) / "config.json"
    _ = config.write_text(f'{{"tools": {{"estate": {{"work_dirs": [{entries}]}}}}}}', encoding="utf-8")


def test_the_tracked_config_names_the_personal_owners_and_orgs() -> None:
    personal = scopes.personal()

    assert personal.owners
    assert personal.orgs
    assert scopes.estate_owners() == {owner.casefold() for owner in (*personal.owners, *personal.orgs)}


def test_org_folders_sit_beside_personal_in_config_order(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _scopes_file(tmp_path, '{"personal": {"owners": ["someone"], "orgs": ["Widgets", "gadget-labs"]}}', monkeypatch)
    personal = tmp_path / "code" / "personal"

    assert scopes.org_dirs(personal) == [tmp_path / "code" / "Widgets", tmp_path / "code" / "gadget-labs"]
    assert scopes.repo_parent_dirs(personal) == [personal, *scopes.org_dirs(personal)]


def test_owners_match_in_any_case(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _scopes_file(tmp_path, '{"personal": {"owners": ["Someone"], "orgs": ["Widgets"]}}', monkeypatch)

    assert scopes.estate_owners() == {"someone", "widgets"}
    assert scopes.is_estate_owner("SOMEONE")
    assert not scopes.is_estate_owner("acme-corp")
    assert scopes.org_folder("widgets") == "Widgets"
    assert scopes.org_folder("someone") is None


def test_a_missing_scopes_file_means_no_personal_owners(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(roots, "checkout_config", lambda name: tmp_path / name)

    assert scopes.estate_owners() == frozenset()
    assert scopes.org_dirs(tmp_path / "personal") == []


def test_a_malformed_scopes_file_is_an_error(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    _scopes_file(tmp_path, '{"personal": {"orgs": "Widgets"}}', monkeypatch)

    with pytest.raises(ValidationError):
        _ = scopes.personal()


def test_no_work_scope_unless_the_machine_configures_one(tmp_path: Path) -> None:
    assert scopes.work_dirs(tmp_path) == ()


def test_work_dirs_are_names_under_the_code_root_or_absolute_paths(tmp_path: Path) -> None:
    elsewhere = tmp_path / "elsewhere" / "acme"
    _machine(["acme", str(elsewhere), " "])

    assert scopes.work_dirs(tmp_path / "code") == (tmp_path / "code" / "acme", elsewhere)


def test_the_workspace_repo_is_unset_unless_the_machine_names_it(tmp_path: Path) -> None:
    assert scopes.workspace_repo(tmp_path) is None

    config = Path(os.environ["WORKSPACE_HOME"]) / "config.json"
    _ = config.write_text('{"tools": {"estate": {"workspace_repo": "workspace"}}}', encoding="utf-8")

    assert scopes.workspace_repo(tmp_path) == tmp_path / "workspace"
