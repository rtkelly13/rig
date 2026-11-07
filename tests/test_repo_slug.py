from __future__ import annotations

import pytest

from rig.repo_slug import RepoSlug

pytestmark = pytest.mark.ci


@pytest.mark.parametrize(
    ("url", "expected"),
    [
        ("https://github.com/rtkelly13/rig", RepoSlug("rtkelly13", "rig")),
        ("https://github.com/rtkelly13/rig.git", RepoSlug("rtkelly13", "rig")),
        ("https://github.com/rtkelly13/rig.git/", RepoSlug("rtkelly13", "rig")),
        ("https://github.com/Owner/Repo/", RepoSlug("Owner", "Repo")),
        ("https://user:token@github.com/o/r.git", RepoSlug("o", "r")),
        ("http://www.github.com/o/r", RepoSlug("o", "r")),
        ("HTTPS://GitHub.com/o/r", RepoSlug("o", "r")),
        ("git+https://github.com/Owner/Repo.git", RepoSlug("Owner", "Repo")),
        ("ssh://git@github.com/rtkelly13/rig.git", RepoSlug("rtkelly13", "rig")),
        ("ssh://git@ssh.github.com:443/o/r.git", RepoSlug("o", "r")),
        ("git@github.com:rtkelly13/shared-utilities.git", RepoSlug("rtkelly13", "shared-utilities")),
        ("git@github.com:ryan/shared-utilities.git\n", RepoSlug("ryan", "shared-utilities")),
        ("github.com:o/r", RepoSlug("o", "r")),
        ("github.com/rtkelly-labs/Iceberg.NET", RepoSlug("rtkelly-labs", "Iceberg.NET")),
        ("  https://github.com/o/my_repo.v2  ", RepoSlug("o", "my_repo.v2")),
        ("https://github.com/o/r?tab=readme#top", RepoSlug("o", "r")),
    ],
)
def test_github_remote_forms_parse(url: str, expected: RepoSlug) -> None:
    assert RepoSlug.from_remote(url) == expected


@pytest.mark.parametrize(
    "url",
    [
        None,
        "",
        "local/my-repo",
        "git@gitlab.com:rtkelly13/rig.git",
        "https://gitlab.com/o/r",
        # Enterprise hosts are not part of the estate.
        "https://github.example.com/o/r.git",
        "git@ghe.corp.internal:o/r.git",
        "https://notgithub.com/o/r",
        # Not a repo root.
        "https://github.com/o",
        "https://github.com/o/r/tree/main",
        "git@github.com:o/r/extra.git",
        "/Users/me/code/personal/rig",
        r"C:\code\personal\rig",
    ],
)
def test_anything_else_is_none(url: str | None) -> None:
    assert RepoSlug.from_remote(url) is None


def test_full_name_keeps_case_and_key_folds_it() -> None:
    slug = RepoSlug("rtkelly-labs", "Iceberg.NET")

    assert slug.full_name == "rtkelly-labs/Iceberg.NET"
    assert str(slug) == "rtkelly-labs/Iceberg.NET"
    assert slug.key == "rtkelly-labs/iceberg.net"
