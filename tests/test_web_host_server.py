"""The rig web host's own files and endpoints: the SPA shell, static files, brand media and live reload."""

from __future__ import annotations

import json
import threading
import urllib.error
import urllib.request
from collections.abc import Iterator
from pathlib import Path

import pytest

from rig.web_host.server import StudioServer

pytestmark = pytest.mark.ci


@pytest.fixture
def base_url(tmp_path: Path) -> Iterator[str]:
    templates = tmp_path / "templates"
    templates.mkdir()
    (templates / "index.html").write_text("<!DOCTYPE html><html><body>[ RIG ]</body></html>")
    (templates / "studio.css").write_text("body { background: #0a0a1a; }")
    (templates / "studio.js").write_text("console.log('studio ready');")
    logo = tmp_path / "assets" / "test-brand" / "test-brand-logo.svg"
    logo.parent.mkdir(parents=True)
    logo.write_text("<svg viewBox='0 0 1 1'></svg>")
    (tmp_path / "outside.txt").write_text("secret")

    server = StudioServer(port=0, assets_root=tmp_path / "assets", templates_dir=templates, db_path=tmp_path / "rig.db")
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


def _get(url: str) -> tuple[int, bytes, str]:
    with urllib.request.urlopen(url) as res:
        return res.status, res.read(), res.headers.get("Content-Type", "")


def test_the_spa_shell_is_served_at_the_root(base_url: str) -> None:
    status, body, ctype = _get(f"{base_url}/")

    assert (status, ctype.startswith("text/html")) == (200, True)
    assert b"[ RIG ]" in body


def test_static_files_come_from_the_templates_folder(base_url: str) -> None:
    assert b"#0a0a1a" in _get(f"{base_url}/static/studio.css")[1]
    assert b"console.log" in _get(f"{base_url}/static/studio.js")[1]


def test_brand_media_is_served_read_only_for_the_estate_cards(base_url: str) -> None:
    status, body, _ = _get(f"{base_url}/media/assets/test-brand/test-brand-logo.svg")

    assert status == 200
    assert body.startswith(b"<svg")


@pytest.mark.parametrize("path", ["/static/../outside.txt", "/media/assets/../outside.txt", "/media/assets/nope.png"])
def test_paths_outside_their_folder_or_missing_are_not_found(base_url: str, path: str) -> None:
    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(f"{base_url}{path}")

    assert err.value.code == 404


def test_live_reload_reports_template_mtimes(base_url: str) -> None:
    data = json.loads(_get(f"{base_url}/api/dev/mtime")[1])

    assert data["mtime"] > 0
    assert data["css_mtime"] > 0
    assert data["is_preview"] is False


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("GET", "/api/projects"),
        ("GET", "/api/diff"),
        ("POST", "/api/snapshot"),
        ("GET", "/media/variations/x.png"),
        ("GET", "/api/presets"),
        ("POST", "/api/feedback"),
    ],
)
def test_asset_studio_endpoints_live_on_their_own_server_not_here(base_url: str, method: str, path: str) -> None:
    data = b"{}" if method == "POST" else None

    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(urllib.request.Request(f"{base_url}{path}", data=data, method=method))

    assert err.value.code == 404
