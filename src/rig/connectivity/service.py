"""Join publisher bindings to observed consumers in DuckDB; no network calls on graph reads."""

from __future__ import annotations

import json
import threading
from dataclasses import asdict, dataclass, field
from datetime import UTC, datetime
from pathlib import Path

import duckdb

from rig.connectivity.dag import condense
from rig.connectivity.manifests import canonical, read_inventory
from rig.connectivity.models import Consumption, Dependency, Graph, Project, Publication
from rig.gh_origin import get_repo_root_and_origin
from rig.repo_slug import RepoSlug
from rig.typed_json import decode, decode_json
from rig.workspace.engine import config_file_path, estate_root_path, load_managed_specs
from rig.workspace.layout import project_dir
from rig.workspace.packages import load_package_bindings

_LOCK = threading.RLock()


@dataclass(frozen=True, slots=True)
class DeclaredProject:
    repo: str
    depends_on: list[str] = field(default_factory=list[str])


@dataclass(frozen=True, slots=True)
class Registry:
    projects: list[DeclaredProject]


@dataclass(frozen=True, slots=True)
class PackageIdentity:
    registry: str
    package: str


def slug(value: str) -> str:
    """Normalize only validated GitHub identities; unknown URLs never become invented nodes."""
    identity = RepoSlug.from_remote(value)
    return f"{identity.owner}/{identity.name}".lower() if identity else value.lower().removesuffix(".git")


def _edges(connection: duckdb.DuckDBPyConnection) -> list[Dependency]:
    cursor = connection.execute("""
        WITH providers AS (
          SELECT registry, package, min(repo) AS provider FROM publications
          GROUP BY registry, package HAVING count(DISTINCT repo) = 1
        )
        SELECT DISTINCT p.provider AS source, c.repo AS target, c.registry, c.package,
                        c.requirement, c.scope, c.evidence
        FROM consumptions c JOIN providers p USING (registry, package)
        WHERE p.provider <> c.repo
        ORDER BY source, target, registry, package, scope, evidence
    """)
    names = [column[0] for column in cursor.description]
    return [decode(Dependency, dict(zip(names, row, strict=True))) for row in cursor.fetchall()]


def build_graph(config: Path | None = None, root: Path | None = None, ledger: Path | None = None) -> Graph:
    """Rebuild one evidence ledger from local manifests; failed scans remain visible per project."""
    cfg = config or config_file_path()
    estate = root or estate_root_path()
    target = ledger or cfg.parent.parent / "temp" / "estate" / "connectivity.duckdb"
    target.parent.mkdir(parents=True, exist_ok=True)
    specs = load_managed_specs(cfg)
    bindings = load_package_bindings(cfg)
    current = get_repo_root_and_origin(cfg.parent.parent)
    current_slug = slug(current.origin_url) if current.origin_url else ""
    roots = {
        slug(spec.repo): (
            cfg.parent.parent if slug(spec.repo) == current_slug else project_dir(estate, spec.name, spec.repo)
        )
        for spec in specs
    }
    publications: list[Publication] = []
    consumptions: list[Consumption] = []
    projects: list[Project] = []
    dependencies: list[Dependency] = []
    for binding in bindings:
        publications.append(
            Publication(
                binding.spec.registry,
                canonical(binding.spec.registry, binding.spec.name),
                slug(binding.repo),
                f"config/managed-projects.json · {binding.spec.manifest}",
            )
        )
    for spec in specs:
        identity = slug(spec.repo)
        local = roots[identity]
        inventory = read_inventory(local, identity) if local.is_dir() else None
        if inventory:
            publications.extend(inventory.publications)
            consumptions.extend(inventory.consumptions)
            for reference in inventory.project_refs:
                for upstream, upstream_root in roots.items():
                    if upstream != identity and reference.target.is_relative_to(upstream_root.resolve()):
                        dependencies.append(
                            Dependency(
                                upstream,
                                identity,
                                "project",
                                reference.target.name,
                                "project reference",
                                "runtime",
                                reference.evidence,
                            )
                        )
        provided = sorted({publication.package for publication in publications if publication.repo == identity})
        projects.append(
            Project(
                identity,
                spec.name,
                spec.description,
                spec.domain,
                spec.stack,
                spec.enabled,
                bool(
                    inventory
                    and not any(warning.startswith("Manifest inventory unavailable") for warning in inventory.warnings)
                ),
                len(inventory.paths) if inventory else 0,
                provided,
                inventory.warnings if inventory else ["Local checkout is unavailable; consumption was not scanned."],
            )
        )
    warnings: list[str] = []
    with _LOCK, duckdb.connect(str(target)) as connection:
        connection.execute(
            "CREATE TABLE IF NOT EXISTS publications(registry VARCHAR, package VARCHAR, repo VARCHAR, evidence VARCHAR)"
        )
        connection.execute(
            "CREATE TABLE IF NOT EXISTS consumptions(registry VARCHAR, package VARCHAR, repo VARCHAR, "
            "requirement VARCHAR, scope VARCHAR, evidence VARCHAR)"
        )
        connection.execute("BEGIN TRANSACTION")
        try:
            connection.execute("DELETE FROM publications")
            connection.execute("DELETE FROM consumptions")
            if publications:
                connection.executemany(
                    "INSERT INTO publications VALUES (?, ?, ?, ?)",
                    [(value.registry, value.package, value.repo, value.evidence) for value in publications],
                )
            if consumptions:
                connection.executemany(
                    "INSERT INTO consumptions VALUES (?, ?, ?, ?, ?, ?)",
                    [
                        (value.registry, value.package, value.repo, value.requirement, value.scope, value.evidence)
                        for value in consumptions
                    ],
                )
            dependencies.extend(_edges(connection))
            cursor = connection.execute(
                "SELECT registry, package FROM publications GROUP BY registry, package HAVING count(DISTINCT repo)>1"
            )
            names = [column[0] for column in cursor.description]
            ambiguous = [decode(PackageIdentity, dict(zip(names, row, strict=True))) for row in cursor.fetchall()]
            warnings.extend(
                f"Ambiguous publisher for {row.registry}:{row.package}; no connection was guessed." for row in ambiguous
            )
            connection.execute("COMMIT")
        except (duckdb.Error, ValueError):
            connection.execute("ROLLBACK")
            raise
    declared = decode_json(Registry, cfg.read_text(encoding="utf-8"))
    known = {project.id for project in projects}
    names = {project.name: project.id for project in projects}
    for project in declared.projects:
        for upstream in project.depends_on:
            source = names.get(upstream, slug(upstream))
            if source in known and source != slug(project.repo):
                dependencies.append(
                    Dependency(
                        source,
                        slug(project.repo),
                        "declared",
                        upstream,
                        "declared dependency",
                        "runtime",
                        "config/managed-projects.json · depends_on",
                    )
                )
            else:
                warnings.append(f"Unknown or self dependency {upstream} for {slug(project.repo)}")
    dependencies.sort(key=lambda edge: (edge.source, edge.target, edge.registry, edge.package, edge.evidence))
    dag = condense(projects, dependencies)
    graph = Graph(projects, dependencies, dag.components, dag.edges, datetime.now(UTC).isoformat(), warnings)
    # The same transient directory owns both the queryable ledger and its typed response snapshot.
    snapshot = target.with_suffix(".json")
    temporary = snapshot.with_suffix(f".{threading.get_ident()}.tmp")
    temporary.write_text(json.dumps(asdict(graph)), encoding="utf-8")
    temporary.replace(snapshot)
    return graph
