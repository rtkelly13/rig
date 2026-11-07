"""Where a whole Windows volume's space went, from a WizTree MFT export.

The rest of `estate` measures the clones it knows about. When the disk is
full the question is wider — which also makes `du`-style walking hopeless: a
929 GB volume holds ~10M files, and a walk of the repo root alone ran 40+
minutes without finishing. WizTree reads the NTFS Master File Table instead and
exports every folder's *allocated* size in under a minute.

Allocated, not logical, is the number that matters. uv builds `.venv`s from
hardlinks into its cache and WinSxS hardlinks into System32; logical size
overstates both by 2-3x, on exactly the directories most tempting to delete.

Three views, each answering a different question:

``tree``      where is it? The largest folders, drilled down from the root.
``partition`` what kind of thing is it? Dev artefacts classified once, at
              their outermost match, so categories never overlap and the
              totals add up.
``hogs``      the known Windows offenders, each with its remedy and whether
              the remedy is safe to run unattended.
"""

from __future__ import annotations

import os
import pathlib
import re
import subprocess
import time
from dataclasses import dataclass

from rig.typed_json import decode

WIZTREE = pathlib.Path(r"C:\Program Files\WizTree\WizTree64.exe")


@dataclass(slots=True, frozen=True)
class Folder:
    path: str
    allocated: int
    size: int
    files: int


@dataclass(slots=True, frozen=True)
class TreeRow:
    """One folder in the drill-down, *level* segments below the drive root."""

    level: int
    folder: Folder


@dataclass(slots=True, frozen=True)
class Hog:
    """A known Windows disk consumer and what to do about it."""

    label: str
    #: Path pattern; `%VAR%` expands from the environment and `*` matches one segment.
    pattern: str
    remedy: str
    #: "safe" regenerates; "review" needs a human look; "never" is not to be deleted by hand.
    safety: str


HOGS: tuple[Hog, ...] = (
    Hog(
        "Docker Desktop disk",
        r"%LOCALAPPDATA%\Docker\wsl",
        "docker system prune -a, then compact the VHDX (prune alone frees nothing)",
        "review",
    ),
    Hog(
        "WSL distro disks",
        r"%LOCALAPPDATA%\Packages\*CanonicalGroupLimited*",
        "wsl --shutdown; wsl --manage <distro> --set-sparse true",
        "review",
    ),
    Hog("WSL disks (new layout)", r"%LOCALAPPDATA%\wsl", "wsl --shutdown; compact or --set-sparse", "review"),
    Hog(
        "JumpCloud ADMU backups",
        r"%LOCALAPPDATA%\JumpCloudADMU",
        "delete *_permission_backup_* (keep the csv/log beside them)",
        "review",
    ),
    Hog("uv cache", r"%LOCALAPPDATA%\uv\cache", "uv cache prune (only frees entries no .venv links to)", "safe"),
    Hog("pnpm store", r"%LOCALAPPDATA%\pnpm\store", "pnpm store prune", "safe"),
    Hog("npm cache", r"%LOCALAPPDATA%\npm-cache", "npm cache clean --force", "safe"),
    Hog("pip cache", r"%LOCALAPPDATA%\pip\cache", "pip cache purge", "safe"),
    Hog("NuGet global packages", r"%USERPROFILE%\.nuget\packages", "dotnet nuget locals all --clear", "safe"),
    Hog("NuGet HTTP cache", r"%LOCALAPPDATA%\NuGet", "dotnet nuget locals all --clear", "safe"),
    Hog("Cargo registry", r"%USERPROFILE%\.cargo\registry", "cargo cache --autoclean (cargo-cache)", "safe"),
    Hog(
        "Playwright browsers",
        r"%LOCALAPPDATA%\ms-playwright",
        "npx playwright uninstall --all; reinstall what is pinned",
        "safe",
    ),
    Hog("Hugging Face cache", r"%USERPROFILE%\.cache\huggingface", "huggingface-cli delete-cache", "review"),
    Hog("User temp", r"%LOCALAPPDATA%\Temp", "delete entries older than a day", "safe"),
    Hog("Windows temp", r"C:\Windows\Temp", "delete entries older than a day (admin)", "safe"),
    Hog(
        "Windows Update downloads",
        r"C:\Windows\SoftwareDistribution\Download",
        "stop wuauserv, clear, start (admin)",
        "safe",
    ),
    Hog("Component store", r"C:\Windows\WinSxS", "Dism /online /Cleanup-Image /StartComponentCleanup", "never"),
    Hog("Installer cache", r"C:\Windows\Installer", "never by hand; PatchCleaner if anything", "never"),
    Hog("Recycle Bin", r"C:\$Recycle.Bin", "Clear-RecycleBin", "safe"),
    Hog("Downloads", r"%USERPROFILE%\Downloads", "estate downloads", "review"),
    Hog("Claude Code projects", r"%USERPROFILE%\.claude\projects", "old session transcripts", "review"),
    Hog("Claude desktop", r"%APPDATA%\Claude", "VM bundles and caches", "review"),
    Hog("VS Code extensions", r"%USERPROFILE%\.vscode\extensions", "uninstall unused; old versions linger", "review"),
    Hog("JetBrains caches", r"%LOCALAPPDATA%\JetBrains", "File > Invalidate Caches; remove old IDE versions", "review"),
    Hog("Chrome profile", r"%LOCALAPPDATA%\Google\Chrome\User Data", "clear browsing data", "review"),
    Hog("Box Drive", r"%USERPROFILE%\Box", "cloud-streamed placeholders: never a space target", "never"),
)


#: Names unambiguous anywhere on the volume.
_ANYWHERE: dict[str, str] = {
    "node_modules": "node_modules",
    ".venv": "python venv",
    "venv": "python venv",
    "__pycache__": "tool caches",
    ".pytest_cache": "tool caches",
    ".ruff_cache": "tool caches",
    ".mypy_cache": "tool caches",
    ".git": ".git object stores",
}
#: Names that only mean "build output" inside the code root (Program Files has a `bin` too).
_CODE_ONLY: dict[str, str] = {
    "temp": "scratch in temp/",
    ".worktrees": "worktrees",
    "packages": "NuGet packages",
    "bin": "build output",
    "obj": "build output",
    "dist": "build output",
    "build": "build output",
    "target": "build output",
    ".next": "build output",
    ".turbo": "build output",
}


def _wiztree_running() -> bool:
    done = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {WIZTREE.name}", "/NH"],
        capture_output=True,
        text=True,
        check=False,
    )
    return WIZTREE.name.casefold() in done.stdout.casefold()


def run_wiztree(drive: str, out: pathlib.Path, *, timeout: float = 900) -> pathlib.Path:
    """Export *drive*'s folders to *out* and wait for the elevated child to finish.

    `/admin=1` relaunches elevated, so the launching process returns within
    seconds while a child keeps writing. Completion is "the child has exited",
    not "launcher exited" and not "file stopped growing".
    """
    if not WIZTREE.is_file():
        msg = f"WizTree not found at {WIZTREE}: winget install --id AntibodySoftware.WizTree"
        raise FileNotFoundError(msg)
    out.unlink(missing_ok=True)
    root = drive.rstrip(":\\/") + ":\\"
    subprocess.run(
        [str(WIZTREE), root, f"/export={out}", "/admin=1", "/exportfolders=1", "/exportfiles=0"],
        check=False,
    )
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(3)
        # The export pauses mid-write while buffering; only the child exiting means done.
        if out.is_file() and out.stat().st_size > 0 and not _wiztree_running():
            return out
    msg = f"WizTree export did not complete within {timeout:.0f}s (was the UAC prompt approved?)"
    raise TimeoutError(msg)


def load(csv: pathlib.Path, *, floor: int = 0) -> list[Folder]:
    """Read a WizTree folder export.

    Row 1 is a banner and folder names contain commas. The header carries four
    drive-level columns only the root row fills, so rows are ragged and the
    dialect sniffer gives up: the schema is pinned instead.
    """
    import duckdb

    con = duckdb.connect()
    rows = con.execute(
        """
        SELECT rtrim("File Name", '\\') AS path, "Allocated" AS allocated, "Size" AS size, "Files" AS files
        FROM read_csv(
            ?, skip = 1, header = true, delim = ',', quote = '"', escape = '"',
            null_padding = true, auto_detect = false,
            columns = {
                'File Name': 'VARCHAR', 'Size': 'BIGINT', 'Allocated': 'BIGINT', 'Modified': 'VARCHAR',
                'Attributes': 'BIGINT', 'Files': 'BIGINT', 'Folders': 'BIGINT', 'DRIVECAPACITY': 'BIGINT',
                'FREESPACE': 'BIGINT', 'USEDSPACE': 'BIGINT', 'RESERVEDSPACE': 'BIGINT'
            }
        )
        WHERE "Allocated" >= ?
        """,
        [str(csv), floor],
    )
    names = [column[0] for column in rows.description]
    return [decode(Folder, dict(zip(names, row, strict=True))) for row in rows.fetchall()]


def _depth(path: str) -> int:
    return path.count("\\")


def _parent(path: str) -> str:
    head, _, _ = path.rpartition("\\")
    return head


def tree(folders: list[Folder], *, min_bytes: int, max_depth: int) -> list[TreeRow]:
    """Depth-first drill-down: each folder's children at or above *min_bytes*, largest first."""
    children: dict[str, list[Folder]] = {}
    roots: list[Folder] = []
    for f in folders:
        if f.allocated < min_bytes:
            continue
        if _depth(f.path) == 0:
            roots.append(f)
        else:
            children.setdefault(_parent(f.path).casefold(), []).append(f)

    out: list[TreeRow] = []

    def walk(node: Folder, level: int) -> None:
        out.append(TreeRow(level=level, folder=node))
        if level >= max_depth:
            return
        for child in sorted(children.get(node.path.casefold(), []), key=lambda c: -c.allocated):
            walk(child, level + 1)

    for root in sorted(roots, key=lambda r: -r.allocated):
        walk(root, 0)
    return out


def _category(path: str, code_root: str) -> str | None:
    name = path.rpartition("\\")[2].casefold()
    if name in _ANYWHERE:
        return _ANYWHERE[name]
    if path.casefold().startswith(code_root) and name in _CODE_ONLY:
        return _CODE_ONLY[name]
    return None


def partition(folders: list[Folder], code_root: pathlib.Path) -> dict[str, int]:
    """Allocated bytes per category, each folder counted once at its outermost match.

    `temp/` holds `packages/` which holds `bin/`; summing them would triple-count.
    Sorted by path, a folder's descendants follow it immediately, so one pass
    with a "consumed prefix" attributes each subtree to its outermost match.
    """
    root = str(code_root).rstrip("\\").casefold() + "\\"
    totals: dict[str, int] = {}
    consumed: str | None = None
    # Sort with the trailing separator: bare, `repo-b` sorts between `repo` and `repo\x`.
    for f in sorted(folders, key=lambda x: x.path.casefold() + "\\"):
        key = f.path.casefold() + "\\"
        if consumed is not None and key.startswith(consumed):
            continue
        consumed = None
        cat = _category(f.path, root)
        if cat is None:
            continue
        totals[cat] = totals.get(cat, 0) + f.allocated
        consumed = key
    code_total = next((f.allocated for f in folders if f.path.casefold() == root.rstrip("\\")), 0)
    if code_total:
        inside = sum(totals.values())
        if inside <= code_total:
            totals["source + everything else (code root)"] = code_total - inside
        else:
            # WizTree charges a hardlinked file to every folder linking it; the root counts it once.
            totals["hardlinks shared across categories (subtract)"] = code_total - inside
    return dict(sorted(totals.items(), key=lambda kv: -kv[1]))


_PERCENT_VAR = re.compile(r"%([^%]+)%")


def _expand(pattern: str) -> str:
    """Expand Windows ``%NAME%`` variables on any platform; POSIX ``expandvars`` ignores them."""
    expanded = _PERCENT_VAR.sub(lambda m: os.environ.get(m.group(1), m.group(0)), pattern)
    return expanded.rstrip("\\")


def _matches(path: str, pattern: str) -> bool:
    import fnmatch

    p_parts = path.casefold().split("\\")
    pat_parts = pattern.casefold().split("\\")
    return len(p_parts) == len(pat_parts) and all(
        fnmatch.fnmatchcase(a, b) for a, b in zip(p_parts, pat_parts, strict=True)
    )


@dataclass(slots=True, frozen=True)
class HogHit:
    """A catalogued hog found on the volume: its allocated total and the folders that matched."""

    hog: Hog
    allocated: int
    paths: tuple[str, ...]


def hogs(folders: list[Folder], catalogue: tuple[Hog, ...] = HOGS) -> list[HogHit]:
    """Each catalogued hog's allocated total and the folders that matched it, largest first."""
    found: list[HogHit] = []
    for hog in catalogue:
        pattern = _expand(hog.pattern)
        depth = pattern.count("\\")
        hits = [f for f in folders if _depth(f.path) == depth and _matches(f.path, pattern)]
        if hits:
            found.append(HogHit(hog=hog, allocated=sum(h.allocated for h in hits), paths=tuple(h.path for h in hits)))
    found.sort(key=lambda hit: -hit.allocated)
    return found


def root_files(folders: list[Folder]) -> int:
    """Bytes in files at the volume root (pagefile.sys, hiberfil.sys, swapfile.sys)."""
    root = next((f for f in folders if _depth(f.path) == 0), None)
    if root is None:
        return 0
    top = sum(f.allocated for f in folders if _depth(f.path) == 1)
    return max(root.allocated - top, 0)
