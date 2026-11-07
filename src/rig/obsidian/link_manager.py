from __future__ import annotations

import re
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

from rig.obsidian.models.types import BrokenLink, FixLinksResult, LinkReport, UnlinkedMention
from rig.obsidian.orphan_manager import get_all_md_files


def resolve_link_target(target: str, valid_paths: set[str], stem_to_paths: dict[str, list[str]]) -> str | None:
    """Simulates Obsidian link resolution."""
    if not target:
        return None
    if target in valid_paths:
        return target
    paths = stem_to_paths.get(target)
    if paths:
        return paths[0]
    return None


def find_broken_links(vault_path: Path) -> list[BrokenLink]:
    """Scans all MD files for wikilinks whose target resolves to no note."""
    md_files = get_all_md_files(vault_path)
    valid_paths = {p.replace(".md", "") for p in md_files}
    stem_to_paths: dict[str, list[str]] = {}
    for rel_p, stem in md_files.items():
        stem_to_paths.setdefault(stem, []).append(rel_p.replace(".md", ""))

    broken: list[BrokenLink] = []
    link_re = re.compile(r"\[\[(.*?)\]\]")

    for rel_path in sorted(md_files.keys()):
        abs_path = vault_path / rel_path
        try:
            lines = abs_path.read_text(encoding="utf-8", errors="ignore").splitlines()
            for i, line in enumerate(lines):
                for match in link_re.finditer(line):
                    link_match = match.group(1)
                    target_raw = link_match.split("|")[0].split("#")[0].strip()
                    if not target_raw:
                        continue
                    resolved = resolve_link_target(target_raw, valid_paths, stem_to_paths)
                    if not resolved:
                        broken.append(
                            BrokenLink(
                                source=rel_path,
                                target=target_raw,
                                line=i + 1,
                                context=line.strip(),
                            )
                        )
        except OSError:
            continue
    return broken


def find_unlinked_mentions(vault_path: Path) -> list[UnlinkedMention]:
    """Identifies note titles that appear in text but are not linked."""
    md_files = get_all_md_files(vault_path)
    stems = {stem: rel for rel, stem in md_files.items() if len(stem) > 4}

    unlinked: list[UnlinkedMention] = []
    for rel_path, content_stem in md_files.items():
        abs_path = vault_path / rel_path
        try:
            content = abs_path.read_text(encoding="utf-8", errors="ignore")
            clean_content = re.sub(r"\[\[.*?\]\]", " ", content)
            for stem, target_rel in stems.items():
                if stem == content_stem:
                    continue
                if f" {stem} " in clean_content or f" {stem}." in clean_content:
                    unlinked.append(
                        UnlinkedMention(source=rel_path, mention=stem, target=target_rel.replace(".md", ""))
                    )
        except OSError:
            continue
    return unlinked


def run_list_links(root: Path, log: Callable[[str], None] | None = None) -> LinkReport:
    vault_path = root / "Vault" if (root / "Vault").exists() else root
    broken = find_broken_links(vault_path)
    unlinked = find_unlinked_mentions(vault_path)

    if log:
        if broken:
            log("[bold red]Broken Links:[/bold red]")
            for b in broken:
                log(f"  {b.source}:{b.line} -> [[{b.target}]]")
        if unlinked:
            log("\n[bold yellow]Unlinked Mentions:[/bold yellow]")
            for u in unlinked[:20]:  # Limit output
                log(f"  {u.source} mentions [[{u.mention}]]")
            if len(unlinked) > 20:
                log(f"  ... and {len(unlinked) - 20} more.")

    return LinkReport(broken=tuple(broken), unlinked=tuple(unlinked))


@dataclass(frozen=True, slots=True)
class _LinkFix:
    """One wikilink rewrite: the link text as written and its replacement."""

    old: str
    new: str


def run_fix_links(root: Path, log: Callable[[str], None] | None = None) -> FixLinksResult:
    vault_path = root / "Vault" if (root / "Vault").exists() else root
    md_files = get_all_md_files(vault_path)
    stem_to_paths: dict[str, list[str]] = {}
    for rel_p, stem in md_files.items():
        stem_to_paths.setdefault(stem, []).append(rel_p.replace(".md", ""))

    broken = find_broken_links(vault_path)
    fixed_count = 0
    to_rewrite: dict[str, list[_LinkFix]] = {}

    for b in broken:
        target = b.target
        stem = target.split("/")[-1]
        matches = stem_to_paths.get(stem, [])
        if len(matches) == 1:
            new_target = matches[0]
            if log:
                log(f"Fixing {b.source}: [[{target}]] -> [[{new_target}]]")
            to_rewrite.setdefault(b.source, []).append(_LinkFix(old=f"[[{target}]]", new=f"[[{new_target}]]"))
            fixed_count += 1

    for rel_path, fixes in to_rewrite.items():
        abs_path = vault_path / rel_path
        content = abs_path.read_text(encoding="utf-8")
        for fix in fixes:
            content = content.replace(fix.old, fix.new)
        abs_path.write_text(content, encoding="utf-8")

    return FixLinksResult(broken_found=len(broken), fixed=fixed_count)
