"""Unit tests for the Asset Studio local HTTP server and REST API."""

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

from rig.image_drift.server import StudioServer

pytestmark = pytest.mark.ci


@pytest.fixture
def test_environment(tmp_path: Path):
    """Set up mock assets, variations, and templates directory."""
    assets_root = tmp_path / "assets"
    variations_root = tmp_path / "temp" / "variations"
    templates_dir = tmp_path / "templates"

    assets_root.mkdir(parents=True)
    variations_root.mkdir(parents=True)
    templates_dir.mkdir(parents=True)

    # Setup mock template files
    (templates_dir / "index.html").write_text("<!DOCTYPE html><html><body>[ ASSET_STUDIO.exe ]</body></html>")
    (templates_dir / "studio.css").write_text("body { background: #0a0a1a; }")
    (templates_dir / "studio.js").write_text("console.log('studio ready');")

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
        "templates_dir": templates_dir,
        "feedback_path": tmp_path / "temp" / "agent_feedback.md",
    }


def find_free_port() -> int:
    import socket

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("", 0))
        return s.getsockname()[1]


@pytest.fixture
def running_server(test_environment):
    port = find_free_port()
    server = StudioServer(
        host="127.0.0.1",
        port=port,
        assets_root=test_environment["assets_root"],
        variations_root=test_environment["variations_root"],
        templates_dir=test_environment["templates_dir"],
        feedback_file=test_environment["feedback_path"],
    )

    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    time.sleep(0.1)  # brief pause to ensure server is listening

    base_url = f"http://127.0.0.1:{port}"
    yield base_url, test_environment

    server.shutdown()
    server.server_close()


def test_serve_index_html(running_server) -> None:
    base_url, _ = running_server
    req = urllib.request.urlopen(f"{base_url}/")
    assert req.status == 200
    content = req.read().decode("utf-8")
    assert "[ ASSET_STUDIO.exe ]" in content
    assert req.headers.get("Content-Type", "").startswith("text/html")


def test_serve_static_files(running_server) -> None:
    base_url, _ = running_server
    css_req = urllib.request.urlopen(f"{base_url}/static/studio.css")
    assert css_req.status == 200
    assert "background: #0a0a1a" in css_req.read().decode("utf-8")

    js_req = urllib.request.urlopen(f"{base_url}/static/studio.js")
    assert js_req.status == 200
    assert "console.log" in js_req.read().decode("utf-8")


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


def test_api_project_detail_endpoint(running_server) -> None:
    base_url, _ = running_server
    req = urllib.request.urlopen(f"{base_url}/api/project/test-brand")
    assert req.status == 200
    proj = json.loads(req.read().decode("utf-8"))
    assert proj["name"] == "test-brand"
    assert proj["master_svg"] == "/media/assets/test-brand/test-brand.svg"


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


def test_api_feedback_endpoint(running_server) -> None:
    base_url, env = running_server
    payload = {
        "project": "test-brand",
        "reference": "01-reference-concept.jpg",
        "candidate": "test-brand.svg",
        "region": "Center Crossover",
        "issue": "Occlusion Gradient Too Light",
        "verdict": "NEEDS_ITERATION",
        "notes": "Deepen the shadow under the center crossover beam.",
        "prompt": "Please adjust the svg stops...",
    }
    data_bytes = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url}/api/feedback",
        data=data_bytes,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    res = urllib.request.urlopen(req)
    assert res.status == 200
    res_data = json.loads(res.read().decode("utf-8"))
    assert res_data["status"] == "ok"

    # Verify feedback file was created
    feedback_file = env["feedback_path"]
    assert feedback_file.exists()
    content = feedback_file.read_text(encoding="utf-8")
    assert "test-brand" in content
    assert "Center Crossover" in content
    assert "NEEDS_ITERATION" in content
    assert "Please adjust the svg stops..." in content


def test_api_dev_mtime_endpoint(running_server) -> None:
    base_url, _env = running_server
    req = urllib.request.Request(f"{base_url}/api/dev/mtime")
    res = urllib.request.urlopen(req)
    assert res.status == 200
    data = json.loads(res.read().decode("utf-8"))
    assert "mtime" in data
    assert isinstance(data["mtime"], float)
    assert data["mtime"] > 0
    assert "css_mtime" in data
    assert isinstance(data["css_mtime"], float)
    assert "is_preview" in data


def _post_export(base_url: str, project: str) -> tuple[int, dict[str, str]]:
    payload = {"project": project, "cut_type": "logo", "image_base64": "data:image/png;base64,iVBORw0KGgo="}
    req = urllib.request.Request(
        f"{base_url}/api/export-3d-render",
        data=json.dumps(payload).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        res = urllib.request.urlopen(req)
    except urllib.error.HTTPError as err:
        return err.code, json.loads(err.read().decode("utf-8"))
    return res.status, json.loads(res.read().decode("utf-8"))


def test_export_3d_render_writes_only_into_a_known_project(running_server) -> None:
    base_url, env = running_server

    status, body = _post_export(base_url, "test-brand")

    assert status == 200
    assert (env["assets_root"] / "test-brand" / "test-brand-logo.png").is_file()
    assert body["path"] == str(env["assets_root"] / "test-brand" / "test-brand-logo.png")


@pytest.mark.parametrize("project", ["../escaped", "unknown-brand", "test-brand/../../escaped"])
def test_export_3d_render_refuses_an_unknown_project(running_server, project: str) -> None:
    base_url, env = running_server
    before = sorted(p.relative_to(env["assets_root"].parent) for p in env["assets_root"].parent.rglob("*"))

    status, body = _post_export(base_url, project)

    assert status == 400
    assert "Unknown project" in body["error"]
    after = sorted(p.relative_to(env["assets_root"].parent) for p in env["assets_root"].parent.rglob("*"))
    assert after == before
