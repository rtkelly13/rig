"""The Python web routes answer only what is still theirs.

Status, live reload, the estate, the SPA document, static files and brand media are served by the C# host
(tests/Rig.Web.Tests).
"""

from __future__ import annotations

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
    server = StudioServer(port=0, db_path=tmp_path / "rig.db")
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_address[1]}"
    finally:
        server.shutdown()
        server.server_close()


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("GET", "/"),
        ("GET", "/static/app_assets/app.js"),
        ("GET", "/media/assets/rig/rig-favicon.svg"),
        ("GET", "/api/projects"),
        ("GET", "/api/diff"),
        ("POST", "/api/snapshot"),
        ("GET", "/media/variations/x.png"),
        ("GET", "/api/presets"),
        ("POST", "/api/feedback"),
    ],
)
def test_paths_served_by_csharp_or_the_asset_studio_are_not_found_here(base_url: str, method: str, path: str) -> None:
    data = b"{}" if method == "POST" else None

    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(urllib.request.Request(f"{base_url}{path}", data=data, method=method))

    assert err.value.code == 404
