"""Asset Studio's HTTP API: catalogs, diffs, snapshots, media and the cross-origin headers the studio page needs."""

from __future__ import annotations

import json
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

import pytest
from PIL import Image

from image_drift.server import AssetStudioServer

pytestmark = pytest.mark.ci


@pytest.fixture
def test_environment(tmp_path: Path):
    """Set up mock assets and variations."""
    assets_root = tmp_path / "assets"
    variations_root = tmp_path / "temp" / "variations"

    assets_root.mkdir(parents=True)
    variations_root.mkdir(parents=True)

    # Setup mock project: test-brand
    proj_dir = assets_root / "test-brand"
    proj_dir.mkdir()
    (proj_dir / "test-brand.svg").write_text("<svg viewBox='0 0 100 100'></svg>")

    # Create dummy images with PIL
    img1 = Image.new("RGB", (100, 100), color=(10, 20, 30))
    img1.save(proj_dir / "test-brand-logo-1000x1000.png")

    ref_dir = proj_dir / "reference"
    ref_dir.mkdir()
    img_ref = Image.new("RGB", (100, 100), color=(12, 22, 32))
    img_ref.save(ref_dir / "01-reference-concept.jpg")

    return {
        "assets_root": assets_root,
        "variations_root": variations_root,
    }


def find_free_port() -> int:
    import socket

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("", 0))
        return s.getsockname()[1]


@pytest.fixture
def running_server(test_environment):
    port = find_free_port()
    server = AssetStudioServer(
        assets_root=test_environment["assets_root"],
        variations_root=test_environment["variations_root"],
        host="127.0.0.1",
        port=port,
    )

    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    time.sleep(0.1)  # brief pause to ensure server is listening

    base_url = f"http://127.0.0.1:{port}"
    yield base_url, test_environment

    server.shutdown()
    server.server_close()


def test_serve_media_files(running_server) -> None:
    base_url, _ = running_server
    img_req = urllib.request.urlopen(f"{base_url}/media/assets/test-brand/reference/01-reference-concept.jpg")
    assert img_req.status == 200
    assert len(img_req.read()) > 0


def test_api_projects_endpoint(running_server) -> None:
    base_url, _ = running_server
    req = urllib.request.urlopen(f"{base_url}/api/projects")
    assert req.status == 200
    data = json.loads(req.read().decode("utf-8"))
    assert "projects" in data
    assert len(data["projects"]) == 1
    proj = data["projects"][0]
    assert proj["name"] == "test-brand"
    assert proj["master_svg"] == "/media/assets/test-brand/test-brand.svg"
    assert len(proj["references"]) == 1
    assert proj["references"][0]["filename"] == "01-reference-concept.jpg"


def test_api_diff_endpoint(running_server) -> None:
    base_url, _ = running_server
    ref_url = "/media/assets/test-brand/reference/01-reference-concept.jpg"
    cand_url = "/media/assets/test-brand/test-brand-logo-1000x1000.png"
    query = urllib.parse.urlencode({"ref": ref_url, "cand": cand_url})

    req = urllib.request.urlopen(f"{base_url}/api/diff?{query}")
    assert req.status == 200
    diff_data = json.loads(req.read().decode("utf-8"))

    assert "ssim" in diff_data
    assert "rmse_pct" in diff_data
    assert "psnr_db" in diff_data
    assert "diff_heatmap_data" in diff_data
    assert diff_data["diff_heatmap_data"].startswith("data:image/png;base64,")


def test_api_snapshot_endpoint(running_server) -> None:
    base_url, env = running_server
    payload = {
        "project": "test-brand",
        "run_id": "test-run-1",
        "step_idx": 1,
        "step_name": "initial-bevel",
        "svg_content": "<svg viewBox='0 0 100 100'><rect width='50' height='50'/></svg>",
        "notes": "Testing snapshot capture",
        "metrics": {"ssim": 0.98, "rmse_pct": 2.1},
    }
    data_bytes = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url}/api/snapshot",
        data=data_bytes,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    res = urllib.request.urlopen(req)
    assert res.status == 200
    res_data = json.loads(res.read().decode("utf-8"))
    assert res_data["status"] == "ok"
    assert res_data["step"]["step_name"] == "initial-bevel"

    # Verify file was written to variations_root
    run_dir = env["variations_root"] / "test-brand" / "test-run-1"
    assert run_dir.exists()
    svg_files = list(run_dir.glob("*.svg"))
    assert len(svg_files) == 1


def test_every_response_allows_the_studio_page_origin(running_server) -> None:
    base_url, _ = running_server
    res = urllib.request.urlopen(f"{base_url}/api/projects")
    assert res.headers.get("Access-Control-Allow-Origin") == "*"

    img = urllib.request.urlopen(f"{base_url}/media/assets/test-brand/test-brand.svg")
    assert img.headers.get("Access-Control-Allow-Origin") == "*"

    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(f"{base_url}/media/assets/missing.png")
    assert err.value.headers.get("Access-Control-Allow-Origin") == "*"


def test_a_json_post_preflight_is_answered(running_server) -> None:
    base_url, _ = running_server
    req = urllib.request.Request(f"{base_url}/api/snapshot", method="OPTIONS")
    res = urllib.request.urlopen(req)

    assert res.status == 204
    assert res.headers.get("Access-Control-Allow-Origin") == "*"
    assert "POST" in res.headers.get("Access-Control-Allow-Methods", "")
    assert res.headers.get("Access-Control-Allow-Headers") == "Content-Type"


@pytest.mark.parametrize("path", ["/media/assets/../outside.txt", "/media/variations/../../outside.txt"])
def test_media_outside_its_root_is_not_found(running_server, path: str) -> None:
    base_url, env = running_server
    (env["assets_root"].parent / "outside.txt").write_text("secret")

    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(f"{base_url}{path}")

    assert err.value.code == 404


@pytest.mark.parametrize(
    ("method", "path"),
    [("GET", "/"), ("GET", "/static/studio.js"), ("GET", "/api/estate"), ("POST", "/api/db/backup")],
)
def test_the_rig_web_host_routes_are_not_served_here(running_server, method: str, path: str) -> None:
    base_url, _ = running_server
    data = b"{}" if method == "POST" else None

    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(urllib.request.Request(f"{base_url}{path}", data=data, method=method))

    assert err.value.code == 404


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("GET", "/api/presets"),
        ("POST", "/api/presets"),
        ("POST", "/api/export-3d-render"),
        ("POST", "/api/feedback"),
        ("GET", "/api/project/test-brand"),
    ],
)
def test_retired_endpoints_are_gone(running_server, method: str, path: str) -> None:
    base_url, env = running_server
    before = sorted(env["assets_root"].rglob("*"))
    data = b"{}" if method == "POST" else None
    req = urllib.request.Request(f"{base_url}{path}", data=data, method=method)

    with pytest.raises(urllib.error.HTTPError) as err:
        urllib.request.urlopen(req)

    assert err.value.code == 404
    assert sorted(env["assets_root"].rglob("*")) == before
