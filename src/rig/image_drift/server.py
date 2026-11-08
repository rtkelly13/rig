"""Local HTTP Server and REST API for Asset Studio visual workbench."""

from __future__ import annotations

import base64
import json
import logging
import mimetypes
import tempfile
import urllib.parse
from collections.abc import Sequence
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import ClassVar, override

from pydantic import JsonValue, ValidationError

from rig.c4_okf.web import serve_c4
from rig.connectivity.web import handle as handle_connectivity
from rig.image_drift.diagnostics import analyze_decomposed_drift
from rig.image_drift.indexer import (
    AssetIndexer,
    ProjectAssetCatalog,
)
from rig.image_drift.metrics import compute_drift
from rig.image_drift.server_models import (
    BackupResponse,
    CatalogResponse,
    ChangeTimestamps,
    DecomposedResponse,
    DiffQuery,
    DriftResponse,
    EstateProjectResponse,
    EstateResponse,
    HotspotResponse,
    MtimeResponse,
    OrcaBadgeConfig,
    ProductionCutResponse,
    ProjectBrandAssets,
    ProjectsResponse,
    ReferenceResponse,
    SnapshotRequest,
    SnapshotStepResponse,
    StatusResponse,
    VariationRunResponse,
    find_project_brand_assets,
    find_project_dir,
)
from rig.image_drift.snapshots import SnapshotManager
from rig.triage.web import handle as handle_triage
from rig.typed_json import decode, decode_json, try_decode_json
from rig.web_routes import Route, dispatch, frontend_files, serve_app
from rig.workspace.engine import config_file_path, load_managed_specs

logger = logging.getLogger(__name__)


class StudioRequestHandler(BaseHTTPRequestHandler):
    """HTTP request handler routing REST API calls and static file delivery."""

    assets_root: ClassVar[Path]
    variations_root: ClassVar[Path]
    templates_dir: ClassVar[Path]
    environment: ClassVar[str] = "PROD"
    is_preview: ClassVar[bool] = False
    server_port: ClassVar[int] = 8090

    @property
    def env_name(self) -> str:
        if isinstance(self.server, StudioServer):
            return self.server.environment
        return self.environment

    @property
    def env_port(self) -> int:
        if isinstance(self.server, StudioServer):
            return self.server.server_port
        return self.server_port

    @property
    def env_is_preview(self) -> bool:
        if isinstance(self.server, StudioServer):
            return self.server.is_preview
        return self.is_preview

    @property
    def env_db_path(self) -> Path | None:
        return self.server.db_path if isinstance(self.server, StudioServer) else None

    @override
    def log_message(self, format: str, *args: object) -> None:
        """Suppress standard HTTP server access logging to keep terminal output clean."""

    def _send_json(self, data: JsonValue, status: int = 200) -> None:
        payload = json.dumps(data, indent=2).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(payload)

    def _send_file(self, file_path: Path, content_type: str | None = None) -> None:
        if not file_path.exists() or not file_path.is_file():
            self.send_error(404, f"File Not Found: {file_path.name}")
            return

        ctype = content_type or mimetypes.guess_type(str(file_path))[0] or "application/octet-stream"
        content = file_path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(content)))
        self.send_header("Cache-Control", "no-cache, no-store, must-revalidate")
        self.end_headers()
        self.wfile.write(content)

    def _compute_change_timestamps(self) -> ChangeTimestamps:
        """Compute latest overall and CSS mtime across bounded critical frontend files."""
        overall: float = 0.0
        css: float = 0.0
        for target in frontend_files(self.templates_dir):
            try:
                m = target.stat().st_mtime
                overall = max(overall, m)
                if target.suffix == ".css":
                    css = max(css, m)
            except OSError:
                continue
        return ChangeTimestamps(overall=overall, css=css)

    def _resolve_asset_path(self, raw_path: str) -> Path | None:
        """Resolve a media/assets or media/variations URL or relative path to a real path."""
        decoded = urllib.parse.unquote(raw_path.strip())

        if decoded.startswith("/media/assets/"):
            rel = decoded[len("/media/assets/") :]
            candidate = (self.assets_root / rel).resolve()
            if candidate.is_relative_to(self.assets_root) and candidate.exists():
                return candidate
        elif decoded.startswith("/media/variations/"):
            rel = decoded[len("/media/variations/") :]
            candidate = (self.variations_root / rel).resolve()
            if candidate.is_relative_to(self.variations_root) and candidate.exists():
                return candidate
        else:
            # Try direct relative to assets_root, then variations_root
            candidate = (self.assets_root / decoded).resolve()
            if candidate.is_relative_to(self.assets_root) and candidate.exists():
                return candidate
            candidate = (self.variations_root / decoded).resolve()
            if candidate.is_relative_to(self.variations_root) and candidate.exists():
                return candidate

        return None

    def _find_project_dir(self, project_name: str) -> Path:
        return find_project_dir(self.assets_root, project_name)

    def _find_project_brand_assets(self, project_name: str) -> ProjectBrandAssets:
        return find_project_brand_assets(self.assets_root, project_name)

    def _serialize_catalog(self, catalog: ProjectAssetCatalog) -> CatalogResponse:
        """Convert a ProjectAssetCatalog into its API response, with media URLs."""
        return CatalogResponse(
            name=catalog.name,
            master_svg=(f"/media/assets/{catalog.name}/{catalog.master_svg.name}" if catalog.master_svg else None),
            master_png=(f"/media/assets/{catalog.name}/{catalog.master_png.name}" if catalog.master_png else None),
            references=[
                ReferenceResponse(
                    sequence_number=ref.sequence_number,
                    filename=ref.filename,
                    description=ref.description,
                    url=f"/media/assets/{catalog.name}/reference/{ref.filename}",
                )
                for ref in catalog.references
            ],
            production_cuts=[
                ProductionCutResponse(
                    cut_type=cut.cut_type,
                    filename=cut.filename,
                    size_bytes=cut.size_bytes,
                    width=cut.width,
                    height=cut.height,
                    format=cut.format,
                    purpose=cut.purpose,
                    description=cut.description,
                    url=(
                        f"/media/assets/{cut.path.relative_to(self.assets_root).as_posix()}"
                        if cut.path.is_relative_to(self.assets_root)
                        else f"/media/assets/{catalog.name}/{cut.filename}"
                    ),
                )
                for cut in catalog.production_cuts
            ],
            variation_runs=[
                VariationRunResponse(
                    run_id=run.run_id,
                    project=run.project,
                    steps=[
                        SnapshotStepResponse(
                            step_idx=step.step_idx,
                            step_name=step.step_name,
                            timestamp=step.timestamp,
                            svg_url=(
                                f"/media/variations/{catalog.name}/{run.run_id}/{step.svg_path.name}"
                                if step.svg_path
                                else None
                            ),
                            png_url=(
                                f"/media/variations/{catalog.name}/{run.run_id}/{step.png_path.name}"
                                if step.png_path
                                else None
                            ),
                            metrics=step.metrics,
                            notes=step.notes,
                        )
                        for step in run.steps
                    ],
                )
                for run in catalog.variation_runs
            ],
        )

    def do_GET(self) -> None:
        if isinstance(self.server, StudioServer) and dispatch(self.server.routes, self, "GET"):
            return
        if isinstance(self.server, StudioServer) and handle_triage(
            self, "GET", urllib.parse.urlparse(self.path).path, self.server.db_path
        ):
            return
        parsed_url = urllib.parse.urlparse(self.path)
        path = parsed_url.path

        # serve_c4 owns /c4/ and /api/c4/*, the experimental C4 viewer.
        if serve_c4(self, parsed_url) or handle_connectivity(self, path) or serve_app(self, path, self.templates_dir):
            return

        # 2. Static Assets (CSS, JS, Fonts)
        if path.startswith("/static/"):
            rel_path = path[len("/static/") :]
            target = (self.templates_dir / rel_path).resolve()
            if target.is_relative_to(self.templates_dir) and target.exists() and target.is_file():
                self._send_file(target)
                return
            self.send_error(404, f"Static file not found: {rel_path}")
            return

        # 3. Media Assets
        if path.startswith("/media/assets/"):
            rel_path = urllib.parse.unquote(path[len("/media/assets/") :])
            target = (self.assets_root / rel_path).resolve()
            if target.is_relative_to(self.assets_root) and target.exists() and target.is_file():
                self._send_file(target)
                return
            self.send_error(404, f"Asset not found: {rel_path}")
            return

        # 4. Media Variations (ephemeral)
        if path.startswith("/media/variations/"):
            rel_path = urllib.parse.unquote(path[len("/media/variations/") :])
            target = (self.variations_root / rel_path).resolve()
            if target.is_relative_to(self.variations_root) and target.exists() and target.is_file():
                self._send_file(target)
                return
            self.send_error(404, f"Variation not found: {rel_path}")
            return

        # 5. REST API: /api/projects
        if path == "/api/projects":
            indexer = AssetIndexer(assets_root=self.assets_root, variations_root=self.variations_root)
            catalogs = indexer.index_all()
            self._send_json(
                ProjectsResponse(projects=[self._serialize_catalog(cat) for cat in catalogs.values()]).model_dump(
                    mode="json"
                )
            )
            return

        # 5b. REST API: /api/estate
        if path == "/api/estate":
            specs = load_managed_specs()
            orca_cfg = config_file_path().parent / "orca.json"
            if not orca_cfg.is_file():
                orca_cfg = Path("config/orca.json")
            orca_badges = try_decode_json(OrcaBadgeConfig, orca_cfg.read_bytes()) if orca_cfg.is_file() else None
            estate_items: list[EstateProjectResponse] = []
            for s in specs:
                assets = self._find_project_brand_assets(s.name)
                repo_badge = orca_badges.repos.get(s.name) if orca_badges else None
                estate_items.append(
                    EstateProjectResponse(
                        name=s.name,
                        repo=s.repo,
                        description=s.description,
                        domain=s.domain,
                        stack=s.stack,
                        enabled=s.enabled,
                        logo_url=assets.logo_url,
                        banner_url=assets.banner_url,
                        orca_group=s.orca_group,
                        color=repo_badge.color if repo_badge else None,
                        icon=repo_badge.icon if repo_badge else None,
                    )
                )
            self._send_json(EstateResponse(projects=estate_items).model_dump(mode="json"))
            return

        # 5c. REST API: /api/status
        if path == "/api/status":
            from rig.db.connection import default_db_path

            self._send_json(
                StatusResponse(
                    environment=self.env_name,
                    port=self.env_port,
                    is_preview=self.env_is_preview,
                    db_path=str(self.env_db_path or default_db_path()),
                ).model_dump(mode="json")
            )
            return

        # 5d. REST API: /api/dev/mtime (live reload polling)
        if path == "/api/dev/mtime":
            ts = self._compute_change_timestamps()
            self._send_json(
                MtimeResponse(
                    mtime=ts.overall,
                    css_mtime=ts.css,
                    is_preview=self.env_is_preview,
                ).model_dump(mode="json")
            )
            return

        # 6. REST API: /api/diff
        if path == "/api/diff":
            query = decode(DiffQuery, urllib.parse.parse_qs(parsed_url.query))
            ref_raw = query.ref[0] if query.ref else None
            cand_raw = query.cand[0] if query.cand else None

            if not ref_raw or not cand_raw:
                self._send_json({"error": "Missing 'ref' or 'cand' query parameter"}, status=400)
                return

            ref_path = self._resolve_asset_path(ref_raw)
            cand_path = self._resolve_asset_path(cand_raw)

            if not ref_path or not cand_path:
                self._send_json(
                    {
                        "error": "Could not resolve ref or cand image path",
                        "ref": ref_raw,
                        "cand": cand_raw,
                    },
                    status=404,
                )
                return

            # If candidate is SVG, check for corresponding PNG raster in the same directory
            cand_raster = cand_path
            if cand_path.suffix.lower() == ".svg":
                # Look for a companion png
                companion = cand_path.with_suffix(".png")
                if companion.exists():
                    cand_raster = companion
                else:
                    # Look for 1000x1000.png or 2000x2000.png in candidate directory
                    proj_pngs = list(cand_path.parent.glob("*1000x1000.png")) or list(
                        cand_path.parent.glob("*2000x2000.png")
                    )
                    if proj_pngs:
                        cand_raster = proj_pngs[0]

            ref_raster = ref_path
            if ref_path.suffix.lower() == ".svg":
                companion = ref_path.with_suffix(".png")
                if companion.exists():
                    ref_raster = companion

            try:
                with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as tmp_diff:
                    tmp_diff_path = Path(tmp_diff.name)

                drift = compute_drift(
                    reference_path=ref_raster,
                    render_path=cand_raster,
                    diff_output_path=tmp_diff_path,
                )

                diff_bytes = tmp_diff_path.read_bytes()
                tmp_diff_path.unlink(missing_ok=True)

                diff_base64 = base64.b64encode(diff_bytes).decode("ascii")
                diff_data_uri = f"data:image/png;base64,{diff_base64}"

                cand_size = cand_path.stat().st_size
                ref_size = ref_path.stat().st_size

                decomp_data: DecomposedResponse | None = None
                try:
                    decomp_report = analyze_decomposed_drift(
                        reference_path=ref_raster,
                        render_path=cand_raster,
                    )
                    decomp_data = DecomposedResponse(
                        luminance_rmse_pct=round(decomp_report.luminance_rmse_pct, 2),
                        chrominance_rmse_pct=round(decomp_report.chrominance_rmse_pct, 2),
                        color_to_geometry_ratio=round(decomp_report.color_to_geometry_ratio, 2),
                        edge_correlation=round(decomp_report.edge_correlation, 1),
                        boundary_dilation_bias=round(decomp_report.boundary_dilation_bias, 3),
                        hotspots=[
                            HotspotResponse(
                                rank=h.rank,
                                x0=h.x0,
                                y0=h.y0,
                                x1=h.x1,
                                y1=h.y1,
                                error_pct=round(h.error_pct, 2),
                                dominant_mode=h.dominant_mode,
                                summary=h.summary,
                            )
                            for h in decomp_report.hotspots
                        ],
                        recommendations=decomp_report.recommendations,
                    )
                except (OSError, ValueError):
                    decomp_data = None

                self._send_json(
                    DriftResponse(
                        rmse_pct=round(drift.rmse_pct, 2),
                        ssim=round(drift.ssim, 4),
                        psnr_db=round(drift.psnr_db, 2) if drift.psnr_db != float("inf") else 999.0,
                        diff_heatmap_data=diff_data_uri,
                        ref_size_bytes=ref_size,
                        cand_size_bytes=cand_size,
                        decomposed=decomp_data,
                    ).model_dump(mode="json")
                )
            except Exception as exc:
                logger.error("Drift request failed", exc_info=True)
                self._send_json({"error": f"Error computing drift: {exc}"}, status=500)
            return

        self.send_error(404, f"Endpoint not found: {path}")

    def do_POST(self) -> None:
        if isinstance(self.server, StudioServer) and dispatch(self.server.routes, self, "POST"):
            return
        if isinstance(self.server, StudioServer) and handle_triage(
            self, "POST", urllib.parse.urlparse(self.path).path, self.server.db_path
        ):
            return
        parsed_url = urllib.parse.urlparse(self.path)
        path = parsed_url.path

        if path == "/api/snapshot":
            try:
                content_length = int(self.headers.get("Content-Length", 0))
                body = self.rfile.read(content_length).decode("utf-8")
                snapshot = decode_json(SnapshotRequest, body)
                project = snapshot.project
                run_id = snapshot.run_id

                if not project or not run_id:
                    self._send_json({"error": "Missing project or run_id"}, status=400)
                    return

                manager = SnapshotManager(variations_root=self.variations_root)
                step = manager.capture_step(
                    project=project,
                    run_id=run_id,
                    step_idx=snapshot.step_idx,
                    step_name=snapshot.step_name,
                    svg_content=snapshot.svg_content,
                    metrics=snapshot.metrics,
                    notes=snapshot.notes,
                )

                self._send_json(
                    {
                        "status": "ok",
                        "step": {
                            "step_idx": step.step_idx,
                            "step_name": step.step_name,
                            "timestamp": step.timestamp,
                            "svg_path": str(step.svg_path) if step.svg_path else None,
                            "metadata_path": str(step.metadata_path) if step.metadata_path else None,
                        },
                    }
                )
            except ValidationError as exc:
                self._send_json({"error": f"Invalid snapshot request: {exc}"}, status=400)
            except Exception as exc:
                logger.error("Snapshot request failed", exc_info=True)
                self._send_json({"error": f"Failed to capture snapshot: {exc}"}, status=500)
            return

        # 2. On-demand SQLite database backup
        if path == "/api/db/backup":
            from rig.db.backup import backup_database

            try:
                backup_file = backup_database(
                    db_path=self.env_db_path,
                    backup_dir=self.env_db_path.parent / "backups" if self.env_db_path else None,
                )
                self._send_json(BackupResponse(status="ok", backup_file=str(backup_file)).model_dump(mode="json"))
            except Exception as exc:
                logger.error("DB backup failed", exc_info=True)
                self._send_json({"error": f"Database backup failed: {exc}"}, status=500)
            return

        self.send_error(404, f"Endpoint not found: {path}")


class StudioServer(ThreadingHTTPServer):
    """Threading HTTP Server running Asset Studio."""

    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 8090,
        is_preview: bool = False,
        assets_root: Path | None = None,
        variations_root: Path | None = None,
        templates_dir: Path | None = None,
        db_path: Path | None = None,
        routes: Sequence[Route] = (),
    ) -> None:
        # Determine defaults relative to project structure
        module_dir = Path(__file__).parent
        pkg_root = module_dir.parent.parent.parent  # shared-utilities repo root

        self.assets_root: Path = assets_root or (pkg_root / "assets")
        self.variations_root: Path = variations_root or (pkg_root / "temp" / "variations")
        self.templates_dir: Path = templates_dir or (module_dir / "templates")
        self.is_preview: bool = is_preview
        self.environment: str = "PREVIEW" if is_preview else "PROD"
        self.server_port: int = port
        self.routes: tuple[Route, ...] = tuple(routes)

        # Initialize and migrate SQLite database
        from rig.db import default_db_path, get_db_connection, run_migrations

        self.db_path: Path = db_path or default_db_path()
        conn = get_db_connection(db_path)
        run_migrations(conn)
        conn.close()

        # Configure RequestHandler class variables
        StudioRequestHandler.assets_root = self.assets_root
        StudioRequestHandler.variations_root = self.variations_root
        StudioRequestHandler.templates_dir = self.templates_dir
        StudioRequestHandler.environment = self.environment
        StudioRequestHandler.is_preview = self.is_preview
        StudioRequestHandler.server_port = self.server_port

        super().__init__((host, port), StudioRequestHandler)
