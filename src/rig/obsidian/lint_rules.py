"""The vault lint's codes, finding records and filename rules."""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path, PurePath

from rig.obsidian.utils import looks_like_urlish_stem, sanitize_filename


@dataclass(frozen=True, slots=True)
class MetadataFailure:
    path: Path
    code: str
    reason: str
    severity: str


@dataclass(frozen=True, slots=True)
class FilenameIssue:
    path: Path
    code: str
    reason: str
    severity: str
    suggested_stem: str
    suggested_path: Path


@dataclass(frozen=True, slots=True)
class ScoredDuplicate:
    path: Path
    score: int


@dataclass(slots=True)
class FolderLintStats:
    """One folder's finding counts, tallied as findings arrive, then scored by `rescore`."""

    metadata_errors: int = 0
    metadata_warnings: int = 0
    filename_issues: int = 0
    duplicates: int = 0
    total_notes: int = 0
    score: float = 100.0

    def rescore(self) -> None:
        """Score 0-100: weighted lint findings per note, less 0.5 per note past 12 for overcrowding."""
        total = self.total_notes
        if total == 0:
            self.score = 100.0
            return
        lint_penalty = (
            self.metadata_errors * 5 + self.metadata_warnings * 2 + self.filename_issues * 2 + self.duplicates * 10
        )
        lint_score = 100.0 - (lint_penalty / total)
        size_penalty = (total - 12) * 0.5 if total > 12 else 0.0
        self.score = max(0.0, lint_score - size_penalty)


LINT_INVALID_FRONTMATTER = "TVS000"
LINT_MISSING_TYPE = "TVS001"
LINT_MISSING_REQUIRED_FIELD = "TVS002"
LINT_EMPTY_REQUIRED_FIELD = "TVS003"
LINT_EMPTY_REQUIRED_LIST = "TVS004"
LINT_UNKNOWN_TYPE = "TVS005"

LINT_FILENAME_URLISH = "TVS101"
LINT_FILENAME_NON_ASCII = "TVS102"
LINT_FILENAME_SEPARATORS = "TVS103"
LINT_FILENAME_REPEATED_SEPARATORS = "TVS104"
LINT_FILENAME_STRAY_DOTS = "TVS105"
LINT_FILENAME_HANDLE_PREFIX = "TVS106"
LINT_FILENAME_SIMPLIFY = "TVS107"
LINT_FILENAME_WINDOWS_PORTABILITY = "TVS108"

LINT_DUPLICATE_NOTE = "TVS201"


_WINDOWS_RESERVED_NAMES = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}


def windows_portability_issue_reason(relative_path: PurePath) -> str | None:
    for part in relative_path.parts:
        if re.search(r'[<>:"/\\|?*]', part):
            return "path is not portable to Windows"
        if part.endswith((" ", ".")):
            return "path is not portable to Windows"
        stem = part.split(".", 1)[0].upper()
        if stem in _WINDOWS_RESERVED_NAMES:
            return "path is not portable to Windows"
    return None


def portable_windows_stem(stem: str) -> str:
    return sanitize_filename(stem.lower())


@dataclass(frozen=True, slots=True)
class FilenameIssueSpec:
    code: str
    reason: str
    severity: str


def filename_issue_specs(
    stem: str, suggested_stem: str, *, relative_path: PurePath | None = None
) -> list[FilenameIssueSpec]:
    """Every filename rule ``stem`` breaks; the simplify rule only when no other one does."""
    lower = stem.lower()
    found: list[FilenameIssueSpec] = []
    if looks_like_urlish_stem(stem) or re.search(r"(?:^|[-_ ])status[-_ ]\d+", lower):
        found.append(FilenameIssueSpec(LINT_FILENAME_URLISH, "filename is URL-shaped", "warning"))
    if re.search(r"[^\x00-\x7F]", stem):
        found.append(FilenameIssueSpec(LINT_FILENAME_NON_ASCII, "filename includes non-ASCII characters", "warning"))
    if "  " in stem or "_" in stem:
        found.append(FilenameIssueSpec(LINT_FILENAME_SEPARATORS, "filename uses inconsistent separators", "warning"))
    if re.search(r"-{2,}", stem):
        found.append(
            FilenameIssueSpec(LINT_FILENAME_REPEATED_SEPARATORS, "filename has repeated separators", "warning")
        )
    if "." in stem:
        found.append(FilenameIssueSpec(LINT_FILENAME_STRAY_DOTS, "filename includes stray dots", "warning"))
    if stem.startswith("@"):
        found.append(FilenameIssueSpec(LINT_FILENAME_HANDLE_PREFIX, "filename includes handle-style prefix", "warning"))
    portability_reason = windows_portability_issue_reason(relative_path) if relative_path is not None else None
    if portability_reason is not None:
        found.append(FilenameIssueSpec(LINT_FILENAME_WINDOWS_PORTABILITY, portability_reason, "warning"))
    if not found and stem != suggested_stem:
        found.append(FilenameIssueSpec(LINT_FILENAME_SIMPLIFY, "filename can be simplified", "warning"))
    return found


def needs_filename_cleanup(stem: str, *, relative_path: PurePath | None = None) -> bool:
    lower = stem.lower()
    return any(
        [
            looks_like_urlish_stem(lower),
            bool(re.search(r"[^\x00-\x7F]", stem)),
            "_" in stem,
            "  " in stem,
            bool(re.search(r"-{2,}", stem)),
            "." in stem,
            bool(
                re.search(
                    (
                        r"(?:^|[-_ ])(?:[a-z0-9-]+(?:\.[a-z0-9-]+)*\."
                        r"(?:com|org|net|io|dev|ai|app|co|uk|eu|fm|me|tv|ly|sh|rs))(?:$|[ .:_-])"
                    ),
                    lower,
                )
            ),
            stem.startswith("@"),
            windows_portability_issue_reason(relative_path) is not None if relative_path is not None else False,
        ]
    )


def suggested_type(rel_path: PurePath) -> str | None:
    """Suggest a type from a note's vault-relative location and name."""
    rel_str = str(rel_path)
    stem = rel_path.stem
    parent = rel_path.parent.name

    # Hub/Index notes
    is_hub = (
        stem in ["Resources", "Areas", "Projects", "Archive"]
        or "Index" in stem
        or "Hub" in stem
        or "Inbox-" in stem
        or parent == stem
        or parent + " Index" == stem
        or (len(rel_path.parts) == 2 and rel_path.parts[0] in ["Resources", "Areas", "Projects"])
    )

    if is_hub:
        if "Resources" in rel_str or "Social Media" in rel_str:
            return "resource-hub"
        if "Projects" in rel_str:
            return "project"
        return "area"

    if "Social Media Posts" in rel_str:
        return "social"
    if "Resources" in rel_str:
        return "resource"
    if "Projects" in rel_str:
        if "Design" in rel_str:
            return "design-note"
        return "project-note"
    if "Areas" in rel_str:
        return "area"
    return None


@dataclass(frozen=True, slots=True)
class MetadataReportItem:
    """One metadata finding in the JSON report, its path relative to the vault."""

    path: str
    code: str
    reason: str
    severity: str


@dataclass(slots=True)
class FilenameReport:
    """One file's filename findings in the JSON report; `codes` fills in as findings arrive."""

    path: str
    severity: str
    reason: str
    suggested_path: str
    codes: list[str] = field(default_factory=list[str])


@dataclass(frozen=True, slots=True)
class MetadataReport:
    errors: tuple[MetadataReportItem, ...]
    warnings: tuple[MetadataReportItem, ...]


@dataclass(frozen=True, slots=True)
class LintReport:
    """The `graph lint --json` report; `asdict` gives its JSON shape."""

    ok: bool
    vault_path: str
    metadata: MetadataReport
    filenames: tuple[FilenameReport, ...]
    duplicates: dict[str, list[str]]
