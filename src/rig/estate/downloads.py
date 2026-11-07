"""Inspection and duplicate analysis for the user's Downloads directory.

Categorizes files into installers, datasets, media, and documents, and verifies
whether files or folders already exist in Google Drive or cloud storage.
"""

from __future__ import annotations

import hashlib
import pathlib
from dataclasses import dataclass
from enum import StrEnum

from rig.estate.disksize import du


class DownloadCategory(StrEnum):
    INSTALLER = "installer"
    DATASET = "dataset"
    MEDIA = "media"
    DOCUMENT = "document"
    ARCHIVE = "archive"
    OTHER = "other"


_INSTALLER_EXTS = {".dmg", ".pkg", ".iso", ".app"}
_ARCHIVE_EXTS = {".zip", ".tar", ".gz", ".tgz", ".bz2", ".7z", ".rar"}
_DATASET_EXTS = {".csv", ".parquet", ".json", ".sql", ".sqlite", ".db", ".tsv"}
_MEDIA_EXTS = {".jpg", ".jpeg", ".png", ".gif", ".mp4", ".mov", ".mkv", ".mp3", ".wav"}
_DOCUMENT_EXTS = {".pdf", ".docx", ".xlsx", ".pptx", ".txt", ".md"}


def classify_path(path: pathlib.Path) -> DownloadCategory:
    """Classify a file or directory based on extension or typical contents."""
    ext = path.suffix.lower()
    if ext in _INSTALLER_EXTS:
        return DownloadCategory.INSTALLER
    if ext in _ARCHIVE_EXTS:
        return DownloadCategory.ARCHIVE
    if ext in _DATASET_EXTS:
        return DownloadCategory.DATASET
    if ext in _MEDIA_EXTS:
        return DownloadCategory.MEDIA
    if ext in _DOCUMENT_EXTS:
        return DownloadCategory.DOCUMENT

    if path.is_dir():
        # Check sample of children to classify directory
        try:
            sample = list(path.iterdir())[:10]
            exts = {child.suffix.lower() for child in sample if child.is_file()}
            if exts & _MEDIA_EXTS:
                return DownloadCategory.MEDIA
            if exts & _DATASET_EXTS:
                return DownloadCategory.DATASET
        except OSError:
            pass

    return DownloadCategory.OTHER


@dataclass(frozen=True)
class DownloadItem:
    """One item in the Downloads directory."""

    name: str
    path: pathlib.Path
    bytes_used: int
    category: DownloadCategory
    is_dir: bool
    cloud_duplicate_path: pathlib.Path | None = None

    @property
    def has_cloud_duplicate(self) -> bool:
        return self.cloud_duplicate_path is not None


def _file_digest(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as fh:
        for chunk in iter(lambda: fh.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _tree_files(root: pathlib.Path) -> dict[str, pathlib.Path]:
    return {str(p.relative_to(root)): p for p in root.rglob("*") if p.is_file() and p.name != ".DS_Store"}


def contents_identical(local: pathlib.Path, remote: pathlib.Path) -> bool:
    """True only if file bytes (or every file in a directory tree) are byte-identical."""
    try:
        if local.is_dir() != remote.is_dir():
            return False
        if not local.is_dir():
            return local.stat().st_size == remote.stat().st_size and _file_digest(local) == _file_digest(remote)
        left, right = _tree_files(local), _tree_files(remote)
        if left.keys() != right.keys():
            return False
        return all(
            left[k].stat().st_size == right[k].stat().st_size and _file_digest(left[k]) == _file_digest(right[k])
            for k in left
        )
    except OSError:
        return False


def scan_downloads(
    downloads_dir: pathlib.Path | None = None,
    *,
    min_size: int = 10 * 1024 * 1024,
    cloud_roots: list[pathlib.Path] | None = None,
) -> list[DownloadItem]:
    """Scan Downloads and flag duplicate locations in cloud storage."""
    dl = downloads_dir or pathlib.Path("~/Downloads").expanduser()
    if not dl.is_dir():
        return []

    if cloud_roots is None:
        drive_photos = pathlib.Path("~/Google Drive/My Drive/Photos").expanduser()
        drive_root = pathlib.Path("~/Google Drive/My Drive").expanduser()
        cloud_roots = [p for p in (drive_photos, drive_root) if p.is_dir()]

    items: list[DownloadItem] = []
    try:
        children = [c for c in dl.iterdir() if not c.name.startswith(".")]
    except OSError:
        return []

    for child in children:
        try:
            is_directory = child.is_dir()
            size = du(child) if is_directory else child.stat().st_size
        except OSError:
            # Broken symlink or unreadable entry: skip it rather than abort the scan.
            continue
        if size < min_size:
            continue

        cat = classify_path(child)
        duplicate_path: pathlib.Path | None = None

        # Check cloud roots for matching folders or files
        for root in cloud_roots:
            candidate = root / child.name
            # Name alone proves nothing; the duplicate claim requires a content match.
            if candidate.exists() and contents_identical(child, candidate):
                duplicate_path = candidate
                break

        items.append(
            DownloadItem(
                name=child.name,
                path=child,
                bytes_used=size,
                category=cat,
                is_dir=is_directory,
                cloud_duplicate_path=duplicate_path,
            )
        )

    items.sort(key=lambda item: -item.bytes_used)
    return items
