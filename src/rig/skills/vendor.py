"""Upstream skills vendoring engine.

Clones or updates upstream repositories (Matt Pocock skills, Poteto pstack, etc.)
into a temporary scratch area, extracts active skills into a gitignored vendor
folder (skills/vendored/), and prepares them for global symlink synchronization.
"""

from __future__ import annotations

import fnmatch
import shutil
import subprocess
from collections.abc import Sequence
from pathlib import Path
from typing import ClassVar

from pydantic import BaseModel, ConfigDict, Field, TypeAdapter, ValidationError
from rich.console import Console

from rig.fs_links import is_link
from rig.skills.engine import get_canonical_repo_skills_dir

console = Console()


class UpstreamVendor(BaseModel):
    """Specification of an upstream repository providing AI agent skills."""

    model_config: ClassVar[ConfigDict] = ConfigDict(frozen=True)

    name: str = Field(..., min_length=1, description="Vendor identifier")
    url: str = Field(..., min_length=1, description="Git clone URL (HTTPS or SSH)")
    folder_name: str = Field(default="", description="Local clone directory name in ~/code/external")
    branch: str = Field(default="main", description="Git branch to track")
    skills_subdir: str = Field(default="skills", description="Subdirectory inside repo containing skills")
    allowlist: Sequence[str] | None = Field(
        default=None,
        description="Explicit allowlist of skills to extract (takes precedence over include)",
    )
    include: Sequence[str] = Field(default_factory=lambda: ["*"], description="Skills to include")
    exclude: Sequence[str] = Field(default_factory=list, description="Skills to exclude")

    @property
    def target_folder_name(self) -> str:
        return self.folder_name or self.name

    @property
    def effective_allowlist(self) -> Sequence[str]:
        """Returns the active allowlist (explicit allowlist if specified, otherwise include)."""
        if self.allowlist is not None:
            return self.allowlist
        return self.include

    def is_skill_included(self, skill_name: str) -> bool:
        """Determines whether a skill name matches the vendor's allowlist and exclude filter rules."""
        for pat in self.exclude:
            if fnmatch.fnmatch(skill_name, pat):
                return False

        allowed = self.effective_allowlist
        if not allowed or "*" in allowed:
            return True

        return any(fnmatch.fnmatch(skill_name, pat) for pat in allowed)


DEFAULT_UPSTREAM_VENDORS: list[UpstreamVendor] = [
    UpstreamVendor(
        name="mattpocock-skills",
        url="https://github.com/mattpocock/skills.git",
        folder_name="skills",
        branch="main",
        skills_subdir="skills",
        include=["*"],
    ),
    UpstreamVendor(
        name="pstack",
        url="https://github.com/backnotprop/pstack.git",
        folder_name="pstack",
        branch="main",
        skills_subdir="skills",
        include=["*"],
    ),
    UpstreamVendor(
        name="visual-explainer",
        url="https://github.com/nicobailon/visual-explainer.git",
        folder_name="visual-explainer",
        branch="main",
        skills_subdir="plugins",
        include=["visual-explainer"],
    ),
]

VENDOR_LIST_ADAPTER = TypeAdapter(list[UpstreamVendor])


def get_default_vendors_config_file() -> Path:
    """Locates the upstream-skills.json registry file."""
    repo_root = Path(__file__).resolve().parents[3]
    candidate = repo_root / "config" / "upstream-skills.json"
    if candidate.is_file():
        return candidate
    return Path.cwd() / "config" / "upstream-skills.json"


def load_upstream_vendors(config_path: Path | None = None) -> list[UpstreamVendor]:
    """Loads and schema-validates configured upstream vendors from config/upstream-skills.json."""
    cfg = config_path or get_default_vendors_config_file()
    if not cfg.is_file():
        return list(DEFAULT_UPSTREAM_VENDORS)

    try:
        content = cfg.read_text(encoding="utf-8")
        vendors = VENDOR_LIST_ADAPTER.validate_json(content)
        return vendors if vendors else list(DEFAULT_UPSTREAM_VENDORS)
    except ValidationError as e:
        console.print(f"[bold red]Schema validation error in {cfg}:[/bold red]\n{e}")
        raise


def get_default_external_clones_dir() -> Path:
    """Returns the external projects directory (~/code/external) for clones."""
    return Path.home() / "code" / "external"


def get_default_vendored_skills_dir() -> Path:
    """Returns the gitignored skills/vendored directory in shared-utilities."""
    return get_canonical_repo_skills_dir() / "vendored"


def clone_or_update_vendor(vendor: UpstreamVendor, clones_dir: Path | None = None) -> Path:
    """Clone or pull the upstream repository in the external clones folder (~/code/external)."""
    base_dir = clones_dir or get_default_external_clones_dir()
    base_dir.mkdir(parents=True, exist_ok=True)
    repo_dir = base_dir / vendor.target_folder_name

    if (repo_dir / ".git").is_dir():
        console.print(f"[cyan]Updating {vendor.name} from {vendor.url}...[/cyan]")
        res = subprocess.run(
            ["git", "-C", str(repo_dir), "pull", "--ff-only"],
            capture_output=True,
            text=True,
        )
        if res.returncode != 0:
            console.print(f"[yellow]Fast-forward failed, fetching origin/{vendor.branch}...[/yellow]")
            subprocess.run(
                ["git", "-C", str(repo_dir), "fetch", "origin", vendor.branch],
                check=False,
                capture_output=True,
            )
            subprocess.run(
                ["git", "-C", str(repo_dir), "reset", "--hard", f"origin/{vendor.branch}"],
                check=False,
                capture_output=True,
            )
        console.print(f"[bold green]✓ Updated {vendor.name}[/bold green]")
    else:
        console.print(f"[cyan]Cloning {vendor.name} from {vendor.url}...[/cyan]")
        res = subprocess.run(
            ["git", "clone", "--depth", "1", "--branch", vendor.branch, vendor.url, str(repo_dir)],
            capture_output=True,
            text=True,
        )
        if res.returncode != 0:
            console.print(f"[red]Failed to clone {vendor.name}: {res.stderr.strip()}[/red]")
            raise RuntimeError(f"Git clone failed for {vendor.name}: {res.stderr.strip()}")
        console.print(f"[bold green]✓ Cloned {vendor.name}[/bold green]")

    return repo_dir


def extract_vendor_skills(
    vendor: UpstreamVendor,
    repo_dir: Path,
    target_vendored_dir: Path | None = None,
) -> list[str]:
    """Extract and copy selected skill packages from the cloned repo into skills/vendored/<vendor.name>/."""
    base_vendored = target_vendored_dir or get_default_vendored_skills_dir()
    vendor_dest = base_vendored / vendor.name
    vendor_dest.mkdir(parents=True, exist_ok=True)

    search_root = repo_dir / vendor.skills_subdir
    if not search_root.is_dir():
        search_root = repo_dir

    ignored_names = frozenset({"node_modules", ".git", "dist", "build", "temp", "__pycache__"})
    extracted: list[str] = []

    for skill_md in sorted(search_root.rglob("SKILL.md")):
        rel_parts = skill_md.relative_to(search_root).parts
        if any(part in ignored_names or part.startswith(".") for part in rel_parts):
            continue

        skill_src_folder = skill_md.parent
        skill_name = skill_src_folder.name

        if not vendor.is_skill_included(skill_name):
            continue

        skill_dest_folder = vendor_dest / skill_name

        if skill_dest_folder.exists():
            if is_link(skill_dest_folder):
                skill_dest_folder.unlink()
            elif skill_dest_folder.is_dir():
                shutil.rmtree(skill_dest_folder)
            else:
                skill_dest_folder.unlink()

        def _ignore_patterns(_path: str, names: list[str]) -> set[str]:
            return {n for n in names if n in ignored_names or n.startswith(".")}

        shutil.copytree(skill_src_folder, skill_dest_folder, ignore=_ignore_patterns)
        extracted.append(skill_name)

    # Clean up any previously extracted skills in vendor_dest that are no longer included
    if vendor_dest.is_dir():
        for existing_child in list(vendor_dest.iterdir()):
            if existing_child.is_dir() and existing_child.name not in extracted:
                shutil.rmtree(existing_child)

    return extracted


def update_all_vendors(
    vendors: Sequence[UpstreamVendor] | None = None,
    clones_dir: Path | None = None,
    target_vendored_dir: Path | None = None,
    config_path: Path | None = None,
) -> dict[str, list[str]]:
    """Update all configured vendor clones and refresh skills/vendored/."""
    vendor_list = list(vendors) if vendors is not None else load_upstream_vendors(config_path=config_path)
    results: dict[str, list[str]] = {}

    for vendor in vendor_list:
        repo_dir = clone_or_update_vendor(vendor, clones_dir=clones_dir)
        skills_copied = extract_vendor_skills(vendor, repo_dir, target_vendored_dir=target_vendored_dir)
        results[vendor.name] = skills_copied
        console.print(f"[green]✓ Extracted {len(skills_copied)} skills from {vendor.name}[/green]")

    return results
