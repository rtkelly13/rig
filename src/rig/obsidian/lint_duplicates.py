"""Notes that duplicate one another by source URL or title, within the PARA roots."""

from __future__ import annotations

import logging
import re
from pathlib import Path

import frontmatter

from rig.obsidian.note_meta import read_note_meta
from rig.obsidian.utils import FRONTMATTER_ERRORS, normalize_url

logger = logging.getLogger(__name__)


def _load_frontmatter(path: Path) -> frontmatter.Post:
    return frontmatter.load(str(path))


def find_duplicates(vault_path: Path) -> dict[str, list[Path]]:
    """Find notes that share the same URL or Title (within PARA roots).

    Implements 'Genuine Linking' logic:
    A Social Media Post sharing a source with a Resource is NOT a duplicate
    IF it contains a wikilink to that Resource.
    """
    url_map: dict[str, list[Path]] = {}
    title_map: dict[str, list[Path]] = {}

    for md_file in vault_path.rglob("*.md"):
        if ".git" in str(md_file) or ".obsidian" in str(md_file) or "Archive" in str(md_file):
            continue

        try:
            content = md_file.read_text(encoding="utf-8")
            meta = read_note_meta(frontmatter.loads(content).metadata)
            url = meta.source
            is_social = meta.type == "social" or "Social Media Posts" in str(md_file)

            # 1. URL Duplication Detection
            if url:
                norm_url = normalize_url(url)
                if norm_url:
                    if norm_url not in url_map:
                        url_map[norm_url] = []
                    url_map[norm_url].append(md_file)

            # 2. Title Duplication Detection (Focus on PARA Resources/Areas/Projects)
            if not is_social:
                title = meta.title or md_file.stem
                if md_file.parent.name == md_file.stem:
                    continue

                if title:
                    norm_title = re.sub(r"\s+\(1\)$", "", title)
                    norm_title = re.sub(r"-[a-f0-9]{4}$", "", norm_title).strip().lower()
                    category = md_file.parent.name
                    title_key = f"title:{category}/{norm_title}"

                    if title_key not in title_map:
                        title_map[title_key] = []
                    title_map[title_key].append(md_file)

        except FRONTMATTER_ERRORS as e:
            logger.error(f"Error reading {md_file}: {e}")

    raw_dupes: dict[str, list[Path]] = {}
    for key, files in url_map.items():
        if len(files) > 1:
            raw_dupes[key] = sorted(list(set(files)))

    for key, files in title_map.items():
        if len(files) > 1:
            if key not in raw_dupes:
                raw_dupes[key] = sorted(list(set(files)))
            else:
                raw_dupes[key] = sorted(list(set(raw_dupes[key] + files)))

    filtered_dupes: dict[str, list[Path]] = {}
    for key, files in raw_dupes.items():
        resources = [f for f in files if "Resources" in str(f)]
        social_posts = [f for f in files if "Social Media Posts" in str(f)]

        if resources and social_posts:
            remaining_social: list[Path] = []
            for sp in social_posts:
                try:
                    sp_meta = read_note_meta(_load_frontmatter(sp).metadata)
                    related = (sp_meta.related_article or "").replace("\n", " ").strip()
                    sp_content = sp.read_text(encoding="utf-8")

                    linked = False
                    for res in resources:
                        res_name = res.name
                        res_stem = res.stem
                        if (
                            f"[[{res_name}]]" in related
                            or f"[[{res_stem}]]" in related
                            or f"[[{res_name}]]" in sp_content
                            or f"[[{res_stem}]]" in sp_content
                        ):
                            linked = True
                            break
                    if not linked:
                        remaining_social.append(sp)
                except FRONTMATTER_ERRORS:
                    remaining_social.append(sp)

            true_dupes = resources + remaining_social
            if len(true_dupes) > 1:
                filtered_dupes[key] = true_dupes
        elif len(files) > 1:
            filtered_dupes[key] = files

    return filtered_dupes
