"""Site groups by host: the config rules and the committed config."""

from __future__ import annotations

import json
import subprocess
import zipfile
from pathlib import Path

import pytest

from desktop_manager.chrome import site_hosts
from desktop_manager.chrome.site_hosts import CONFIG_NAME, load_site_hosts
from rig.workspace.engine import config_file_path

pytestmark = pytest.mark.ci


def config(tmp_path: Path, groups: dict[str, list[str]]) -> Path:
    path = tmp_path / CONFIG_NAME
    path.write_text(json.dumps({"groups": groups}), encoding="utf-8")
    return path


def test_committed_config_loads() -> None:
    sites = load_site_hosts(config_file_path().parent / CONFIG_NAME)
    assert sites.site_for("api.github.com") == "Dev"


def test_longest_domain_wins(tmp_path: Path) -> None:
    sites = load_site_hosts(config(tmp_path, {"Google": ["google.com"], "AI": ["gemini.google.com"]}))
    assert sites.site_for("gemini.google.com") == "AI"
    assert sites.site_for("mail.google.com") == "Google"
    assert sites.site_for("notgoogle.com") is None


def test_a_domain_in_two_groups_is_rejected(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="npmjs.com is already in Dev"):
        load_site_hosts(config(tmp_path, {"Dev": ["npmjs.com"], "Other": ["npmjs.com"]}))


@pytest.mark.parametrize("domain", ["NPMJS.com", "localhost", "https://npmjs.com", "npmjs"])
def test_domains_must_be_lowercase_hostnames(tmp_path: Path, domain: str) -> None:
    with pytest.raises(ValueError, match="not a lowercase domain"):
        load_site_hosts(config(tmp_path, {"Dev": [domain]}))


def test_without_a_checkout_the_packaged_copy_is_used(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    packaged = config(tmp_path, {"Dev": ["github.com"]})
    monkeypatch.setattr("rig.workspace.engine.config_file_path", lambda: tmp_path / "missing" / "managed-projects.json")
    monkeypatch.setattr(site_hosts, "PACKAGED_CONFIG", packaged)
    assert site_hosts.default_config_path() == packaged


def test_the_wheel_puts_the_config_where_the_loader_looks(tmp_path: Path) -> None:
    root = Path(__file__).parents[2]
    subprocess.run(["uv", "build", "--wheel", "--out-dir", str(tmp_path), str(root)], check=True, capture_output=True)
    (wheel,) = tmp_path.glob("*.whl")
    with zipfile.ZipFile(wheel) as archive:
        assert "desktop_manager/chrome/chrome-site-groups.json" in archive.namelist()
    assert site_hosts.PACKAGED_CONFIG.parts[-3:] == ("desktop_manager", "chrome", CONFIG_NAME)
