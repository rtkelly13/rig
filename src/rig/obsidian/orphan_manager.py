from __future__ import annotations

import os
from collections.abc import Callable
from dataclasses import dataclass, field
from pathlib import Path

from pydantic import StrictBool, StrictStr

from rig.obsidian.config import get_vault_path
from rig.typed_json import Lenient, try_decode_json


def get_all_md_files(vault_path: Path) -> dict[str, str]:
    """Returns a map of relative_path -> filename_stem."""
    md_files: dict[str, str] = {}
    for root, _, files in os.walk(vault_path):
        for f in files:
            if f.endswith(".md"):
                p = Path(root) / f
                rel_p = p.relative_to(vault_path)
                md_files[rel_p.as_posix()] = p.stem
    return md_files


def find_orphans(vault_path: Path, exclude_patterns: list[str] | None = None) -> list[str]:
    """Returns a list of relative paths (with .md) for orphaned notes."""
    if exclude_patterns is None:
        exclude_patterns = ["Templates/", "README.md", "Archive/", "Root File", "Home", ".obsidian/"]

    # 1. Prefer the desktop app's own answer, but only for the vault it has open —
    #    see get_native_orphans. For any other vault it would answer about the wrong one.
    native_orphans = get_native_orphans(vault_path)
    if native_orphans:
        orphans = [o for o in native_orphans if not any(p in o for p in exclude_patterns)]
        return sorted(orphans)

    # 2. Regex-based scan (handles path-qualified links like [[Resources/Books]])
    md_files = get_all_md_files(vault_path)
    all_references = _get_all_references(vault_path, md_files)

    orphans: list[str] = []
    for rel_path, stem in md_files.items():
        path_no_ext = rel_path.replace(".md", "")
        is_referenced = (path_no_ext in all_references) or (stem in all_references)
        if not is_referenced:
            if any(p in rel_path for p in exclude_patterns):
                continue
            orphans.append(rel_path)

    return sorted(orphans)


def _get_all_references(vault_path: Path, md_files: dict[str, str]) -> set[str]:
    """Collect all wikilink targets via regex."""
    import re
    import urllib.parse

    all_references: set[str] = set()
    link_re = re.compile(r"!?\[\[(.*?)\]\]")
    valid_stems = set(md_files.values())
    valid_paths = {p.replace(".md", "") for p in md_files}

    for rel_path in md_files:
        abs_path = vault_path / rel_path
        try:
            content = abs_path.read_text(encoding="utf-8", errors="ignore")
            for match in link_re.finditer(content):
                link_match = match.group(1)
                target_raw = link_match.split("|")[0].strip()
                if not target_raw:
                    continue
                target_unquoted = urllib.parse.unquote(target_raw)
                if target_raw in valid_stems or target_raw in valid_paths:
                    all_references.add(target_raw)
                elif target_unquoted in valid_stems or target_unquoted in valid_paths:
                    all_references.add(target_unquoted)
                elif "#" in target_raw:
                    base = target_raw.split("#")[0].strip()
                    if base in valid_stems or base in valid_paths:
                        all_references.add(base)
        except OSError:
            continue
    return all_references


def link_orphan_to_parent(vault_path: Path, orphan_rel_path: str) -> str | None:
    """
    Attempts to link an orphan to its nearest parent hub.
    """
    parts = orphan_rel_path.split("/")
    for i in range(len(parts) - 1, 0, -1):
        parent_hub_rel = "/".join(parts[:i]) + ".md"
        parent_hub_abs = vault_path / parent_hub_rel
        if parent_hub_abs.exists():
            try:
                with parent_hub_abs.open("a", encoding="utf-8") as f:
                    f.write(f"\n- [[{orphan_rel_path}]]")
                return parent_hub_rel
            except OSError:
                return None
    return None


@dataclass(frozen=True, slots=True)
class _VaultEntry:
    path: StrictStr = ""
    open: StrictBool = False


@dataclass(frozen=True, slots=True)
class _VaultRegistry:
    """obsidian.json: the vaults the desktop app knows, keyed by an opaque id; a misshapen entry reads as None."""

    vaults: dict[str, Lenient[_VaultEntry]] = field(default_factory=dict[str, _VaultEntry | None])


def _obsidian_open_vault() -> Path | None:
    """The vault the Obsidian desktop app currently has open, if any."""
    registry = Path.home() / "Library" / "Application Support" / "obsidian" / "obsidian.json"
    if not registry.is_file():
        return None
    try:
        text = registry.read_text(encoding="utf-8")
    except OSError:
        return None
    registry_file = try_decode_json(_VaultRegistry, text)
    if registry_file is None:
        return None
    for vault in registry_file.vaults.values():
        if vault is not None and vault.open and vault.path:
            return Path(vault.path)
    return None


def get_native_orphans(root: Path | None = None, timeout: float = 15.0) -> list[str]:
    """Orphans as the Obsidian desktop app sees them.

    Two constraints this call has to respect, both learned the hard way:

    **It reports on whichever vault Obsidian has open**, and takes no path argument.
    Asking it about any other vault returns a confident answer about the wrong one —
    which silently broke cross-checks against a temporary or secondary vault. So it
    returns nothing unless `root` is the open vault.

    **It is the desktop binary, not a CLI daemon.** It usually answers in a second or
    two, but nothing guarantees that, and without a timeout a stall is unbounded.
    """
    if root is not None:
        open_vault = _obsidian_open_vault()
        if open_vault is None or open_vault.resolve() != Path(root).resolve():
            return []

    import subprocess

    try:
        res = subprocess.run(["obsidian", "orphans"], capture_output=True, text=True, timeout=timeout, check=False)
    except (OSError, subprocess.SubprocessError):
        return []
    if res.returncode != 0:
        return []
    return [line.strip() for line in res.stdout.splitlines() if line.strip()]


def run_list_orphans(root: Path, log: Callable[[str], None] | None = None, compare: bool = False) -> list[str]:
    vault_path = get_vault_path()
    custom_orphans = find_orphans(vault_path)

    if not compare:
        if log:
            for o in custom_orphans:
                log(o)
        return custom_orphans

    native_orphans = get_native_orphans(root)

    # Custom orphans now already have .md
    custom_set = set(custom_orphans)
    native_set = set(native_orphans)

    # Cross-check results
    both = sorted(list(custom_set & native_set))
    only_custom = sorted(list(custom_set - native_set))
    only_native = sorted(list(native_set - custom_set))

    if log:
        from rich.console import Console
        from rich.table import Table

        console = Console()

        table = Table(title="Orphan Cross-Check (Custom vs Native)")
        table.add_column("Path", style="cyan")
        table.add_column("Custom", justify="center")
        table.add_column("Native", justify="center")

        for p in both:
            table.add_row(p, "[green]✓[/green]", "[green]✓[/green]")
        for p in only_custom:
            table.add_row(p, "[green]✓[/green]", "[red]×[/red]")
        for p in only_native:
            # Filter out things we usually exclude (Templates, etc)
            if any(exc in p for exc in ["Templates/", "README.md", ".obsidian/"]):
                continue
            table.add_row(p, "[red]×[/red]", "[green]✓[/green]")

        console.print(table)
        console.print(
            f"\n[bold]Summary:[/bold] Both: {len(both)}"
            f" | Only Custom: {len(only_custom)} | Only Native (Filtered): {len(only_native)}"
        )

    return custom_orphans


@dataclass(frozen=True, slots=True)
class OrphanLinkResult:
    """Output of ``run_link_orphans``."""

    orphans_found: int
    linked: int


@dataclass(frozen=True, slots=True)
class RootHubResult:
    """Output of ``run_root_hub``."""

    hubs_created: int
    links_added: int


def run_link_orphans(root: Path, log: Callable[[str], None] | None = None) -> OrphanLinkResult:
    vault_path = root
    orphans = find_orphans(vault_path)
    linked_count = 0
    for o in orphans:
        hub = link_orphan_to_parent(vault_path, o)
        if hub:
            linked_count += 1
            if log:
                log(f"Linked {o} -> {hub}")
    return OrphanLinkResult(orphans_found=len(orphans), linked=linked_count)


def run_root_hub(log: Callable[[str], None] | None = None) -> RootHubResult:
    """Create root hubs for PARA folders and link first-level notes."""
    vault_path = get_vault_path()
    para_folders = ["Projects", "Areas", "Resources", "Archives"]
    created_hubs = 0
    links_added = 0

    # Ensure Home.md exists
    home_path = vault_path / "Home.md"
    if not home_path.exists():
        home_path.write_text("# 🏠 Home\n\n## PARA Root\n", encoding="utf-8")
        created_hubs += 1

    for folder in para_folders:
        folder_path = vault_path / folder
        if not folder_path.exists():
            continue

        hub_path = vault_path / f"{folder}.md"
        if not hub_path.exists():
            hub_path.write_text(f"# 🗺️ {folder}\n\n[[Home|🔙 Back to Home]]\n\n## Content\n", encoding="utf-8")
            created_hubs += 1
            # Link Hub to Home
            home_content = home_path.read_text(encoding="utf-8")
            if f"[[{folder}]]" not in home_content:
                with home_path.open("a", encoding="utf-8") as f:
                    f.write(f"- [[{folder}]]\n")

        # Find first-level notes in this folder that are orphans
        orphans = find_orphans(vault_path)
        for o in orphans:
            # Check if it's a direct child of this PARA folder
            # e.g. "Areas/Career.md"
            parts = o.split("/")
            if len(parts) == 2 and parts[0] == folder:
                hub_content = hub_path.read_text(encoding="utf-8")
                link = f"[[{o}]]"
                if link not in hub_content:
                    with hub_path.open("a", encoding="utf-8") as f:
                        f.write(f"- {link}\n")
                    links_added += 1
                    if log:
                        log(f"Linked {o} -> {folder}.md")

    return RootHubResult(hubs_created=created_hubs, links_added=links_added)
