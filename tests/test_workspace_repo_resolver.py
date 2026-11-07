"""Tests for GitHub canonical repo resolution and folder health checking."""

from __future__ import annotations

import datetime as dt
import json
import subprocess
from pathlib import Path

from rig.proc import GhResult
from rig.repo_slug import RepoSlug
from rig.workspace.repo_resolver import (
    CacheEntry,
    CanonicalRepo,
    build_batch_query,
    check_estate_repos,
    check_repo_folder,
    parse_batch_response,
    read_cache,
    resolve_canonical_repos,
    write_cache,
)


def test_build_batch_query() -> None:
    query = build_batch_query([RepoSlug("rtkelly13", "shared-utilities"), RepoSlug("rtkelly13", "blog")])
    assert 'r0: repository(owner: "rtkelly13", name: "shared-utilities")' in query
    assert 'r1: repository(owner: "rtkelly13", name: "blog")' in query
    assert "nameWithOwner" in query


def test_parse_batch_response() -> None:
    raw = json.dumps(
        {
            "data": {
                "r0": {
                    "name": "rig",
                    "nameWithOwner": "rtkelly13/rig",
                    "isArchived": False,
                    "url": "https://github.com/rtkelly13/rig",
                },
                "r1": {
                    "name": "blog",
                    "nameWithOwner": "rtkelly13/blog",
                    "isArchived": False,
                    "url": "https://github.com/rtkelly13/blog",
                },
                "r2": None,
            }
        }
    )
    slugs = [
        RepoSlug("rtkelly13", "shared-utilities"),
        RepoSlug("rtkelly13", "blog"),
        RepoSlug("rtkelly13", "missing"),
    ]
    result = parse_batch_response(raw, slugs)

    r0 = result[RepoSlug("rtkelly13", "shared-utilities")]
    assert r0 is not None
    assert r0.canonical_name == "rig"
    assert r0.queried_name == "shared-utilities"
    assert r0.is_renamed is True
    assert r0.name_with_owner == "rtkelly13/rig"

    r1 = result[RepoSlug("rtkelly13", "blog")]
    assert r1 is not None
    assert r1.canonical_name == "blog"
    assert r1.is_renamed is False

    assert result[RepoSlug("rtkelly13", "missing")] is None


def test_cache_roundtrip(tmp_path: Path) -> None:
    cache_file = tmp_path / "repo-cache.json"
    now = dt.datetime.now(dt.UTC)

    entries = {
        "rtkelly13/shared-utilities": CacheEntry(
            repo=CanonicalRepo(
                owner="rtkelly13",
                canonical_name="rig",
                name_with_owner="rtkelly13/rig",
                queried_name="shared-utilities",
                is_archived=False,
                url="https://github.com/rtkelly13/rig",
            ),
            fetched_at=now,
        )
    }

    write_cache(cache_file, entries)
    loaded = read_cache(cache_file)

    assert "rtkelly13/shared-utilities" in loaded
    entry = loaded["rtkelly13/shared-utilities"]
    assert entry.repo.canonical_name == "rig"
    assert entry.repo.queried_name == "shared-utilities"
    assert entry.repo.is_renamed is True
    assert (now - entry.fetched_at).total_seconds() < 2


def test_resolve_with_cache_and_mock_runner(tmp_path: Path) -> None:
    cache_file = tmp_path / "cache.json"
    queries_run: list[str] = []

    def fake_runner(query: str) -> GhResult:
        queries_run.append(query)
        payload = {
            "data": {
                "r0": {
                    "name": "rig",
                    "nameWithOwner": "rtkelly13/rig",
                    "isArchived": False,
                    "url": "https://github.com/rtkelly13/rig",
                }
            }
        }
        return GhResult(0, json.dumps(payload), "")

    slug = RepoSlug("rtkelly13", "shared-utilities")

    # First call: cache miss, runner called
    res = resolve_canonical_repos(
        [slug],
        runner=fake_runner,
        cache_file=cache_file,
    )
    assert len(queries_run) == 1
    r0 = res[slug]
    assert r0 is not None
    assert r0.canonical_name == "rig"

    # Second call: cache hit, runner NOT called
    res2 = resolve_canonical_repos(
        [slug],
        runner=fake_runner,
        cache_file=cache_file,
    )
    assert len(queries_run) == 1
    r1 = res2[slug]
    assert r1 is not None
    assert r1.canonical_name == "rig"

    # Force refresh: bypasses cache, calls runner again
    res3 = resolve_canonical_repos(
        [slug],
        runner=fake_runner,
        cache_file=cache_file,
        force_refresh=True,
    )
    assert len(queries_run) == 2
    r2 = res3[slug]
    assert r2 is not None
    assert r2.canonical_name == "rig"


def test_check_repo_folder_detects_renamed(tmp_path: Path) -> None:
    # Set up a repo named 'shared-utilities' whose upstream is 'rig'
    repo_dir = tmp_path / "shared-utilities"
    repo_dir.mkdir()
    subprocess.run(["git", "init"], cwd=repo_dir, capture_output=True, check=True)
    subprocess.run(
        ["git", "remote", "add", "origin", "git@github.com:rtkelly13/shared-utilities.git"],
        cwd=repo_dir,
        capture_output=True,
        check=True,
    )

    canonical = CanonicalRepo(
        owner="rtkelly13",
        canonical_name="rig",
        name_with_owner="rtkelly13/rig",
        queried_name="shared-utilities",
        is_archived=False,
    )

    check = check_repo_folder(
        repo_dir,
        canonical=canonical,
    )
    assert check.status == "folder_renamed"
    assert check.canonical_name == "rig"
    assert not check.is_match


def test_check_repo_folder_live_git(tmp_path: Path) -> None:
    # 1. OK case: folder matches canonical and remote matches canonical
    ok_dir = tmp_path / "rig"
    ok_dir.mkdir()
    subprocess.run(["git", "init"], cwd=ok_dir, capture_output=True, check=True)
    subprocess.run(
        ["git", "remote", "add", "origin", "git@github.com:rtkelly13/rig.git"],
        cwd=ok_dir,
        capture_output=True,
        check=True,
    )

    canonical_rig = CanonicalRepo(
        owner="rtkelly13",
        canonical_name="rig",
        name_with_owner="rtkelly13/rig",
        queried_name="rig",
    )
    check_ok = check_repo_folder(ok_dir, canonical=canonical_rig)
    assert check_ok.is_match is True
    assert check_ok.status == "ok"
    assert check_ok.canonical_name == "rig"

    # 2. Renamed case: folder is 'shared-utilities', upstream is 'rig'
    renamed_dir = tmp_path / "shared-utilities"
    renamed_dir.mkdir()
    subprocess.run(["git", "init"], cwd=renamed_dir, capture_output=True, check=True)
    subprocess.run(
        ["git", "remote", "add", "origin", "git@github.com:rtkelly13/shared-utilities.git"],
        cwd=renamed_dir,
        capture_output=True,
        check=True,
    )

    canonical_renamed = CanonicalRepo(
        owner="rtkelly13",
        canonical_name="rig",
        name_with_owner="rtkelly13/rig",
        queried_name="shared-utilities",
    )
    check_renamed = check_repo_folder(renamed_dir, canonical=canonical_renamed)
    assert check_renamed.is_match is False
    assert check_renamed.status == "folder_renamed"
    assert "named after a previous repository name" in check_renamed.detail
    assert check_renamed.canonical_name == "rig"

    # 3. Remote outdated case: folder is 'rig', but remote still points to 'shared-utilities'
    rig_dir = tmp_path / "rig-dir"
    rig_dir.mkdir()
    subprocess.run(["git", "init"], cwd=rig_dir, capture_output=True, check=True)
    subprocess.run(
        ["git", "remote", "add", "origin", "git@github.com:rtkelly13/shared-utilities.git"],
        cwd=rig_dir,
        capture_output=True,
        check=True,
    )
    canonical_rig_from_old = CanonicalRepo(
        owner="rtkelly13",
        canonical_name="rig-dir",
        name_with_owner="rtkelly13/rig-dir",
        queried_name="shared-utilities",
    )
    check_remote_outdated = check_repo_folder(rig_dir, canonical=canonical_rig_from_old)
    assert check_remote_outdated.is_match is True
    assert check_remote_outdated.status == "remote_outdated"
    assert "Remote URL points to" in check_remote_outdated.detail

    # 4. Estate scan
    estate_root = tmp_path / "estate"
    estate_root.mkdir()
    repo_in_estate = estate_root / "rig"
    repo_in_estate.mkdir()
    subprocess.run(["git", "init"], cwd=repo_in_estate, capture_output=True, check=True)
    subprocess.run(
        ["git", "remote", "add", "origin", "git@github.com:rtkelly13/rig.git"],
        cwd=repo_in_estate,
        capture_output=True,
        check=True,
    )

    def mock_runner(query: str) -> GhResult:
        payload = {
            "data": {
                "r0": {
                    "name": "rig",
                    "nameWithOwner": "rtkelly13/rig",
                    "isArchived": False,
                    "url": "https://github.com/rtkelly13/rig",
                }
            }
        }
        return GhResult(0, json.dumps(payload), "")

    checks = check_estate_repos(estate_root, runner=mock_runner, cache_file=tmp_path / "cache.json")
    assert len(checks) == 1
    assert checks[0].folder_name == "rig"
    assert checks[0].status == "ok"
