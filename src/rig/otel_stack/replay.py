"""Replay the collector's logs and metrics files into ClickHouse, each line once.

Spans have an identity ClickHouse can be asked about; log records and metric
points do not, so these go by position instead. Every file is tracked by its
inode with the byte offset already sent: the collector rotates by **renaming**
the active file, which keeps the inode, so lines sent while a file was
`logs.jsonl` are not sent again once it becomes `logs-<time>.jsonl`. The offset
moves past each line as soon as the collector accepts it, so a rejection part way
through a file resends nothing next time. Only complete lines are sent, never
the torn tail of a file still being written.

A line the collector accepted is never retried: if its export to ClickHouse then
fails, that line is missing rather than duplicated.

An inode alone does not identify a file: once retention deletes a rotated file,
the filesystem can hand its inode to a new one. Each offset therefore also keeps a
hash of the file's opening bytes, and resumes only when the file still starts the
same way (a rename keeps its bytes; a new file does not). Offsets for inodes no
longer present are dropped at the end of each run.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

from rig.otel_stack.backfill import Sender
from rig.otel_stack.paths import clickhouse_home, data_dir
from rig.typed_json import try_decode_json

SIGNALS = ("logs", "metrics")
STATE_FILE = "replay-state.json"


#: How much of a file's start is hashed to tell it from a later file on the same inode.
HEAD_BYTES = 4096


@dataclass(frozen=True, slots=True)
class Offset:
    inode: int
    sent: int
    #: sha256 of the first `min(sent, HEAD_BYTES)` bytes; "" in state written before it existed.
    head: str = ""


def head_hash(raw: bytes, sent: int) -> str:
    return hashlib.sha256(raw[: min(sent, HEAD_BYTES)]).hexdigest()


@dataclass(frozen=True, slots=True)
class ReplayResult:
    path: Path
    lines: int
    error: str = ""


def files_for(signal: str, folder: Path | None = None) -> list[Path]:
    root = folder or data_dir()
    return sorted(root.glob(f"{signal}*.jsonl")) if root.is_dir() else []


def read_offsets(path: Path) -> dict[str, Offset]:
    try:
        text = path.read_text(encoding="utf-8")
    except OSError:
        return {}
    return try_decode_json(dict[str, Offset], text) or {}


def write_offsets(path: Path, offsets: dict[str, Offset]) -> None:
    payload = {key: {"inode": o.inode, "sent": o.sent, "head": o.head} for key, o in sorted(offsets.items())}
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    _ = temporary.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    _ = temporary.replace(path)


def replay(
    sender: Sender,
    *,
    folder: Path | None = None,
    state_path: Path | None = None,
    dry_run: bool = False,
) -> list[ReplayResult]:
    """Send the unsent complete lines of every logs and metrics file. Stops at the first rejection."""
    offsets_path = state_path or clickhouse_home() / STATE_FILE
    offsets = read_offsets(offsets_path)
    results: list[ReplayResult] = []
    present: set[str] = set()
    for signal in SIGNALS:
        for path in files_for(signal, folder):
            present.update(_inode_key(path))
            result = _replay_file(path, signal, sender, offsets, dry_run)
            results.append(result)
            if not dry_run:
                write_offsets(offsets_path, offsets)
            if result.error:
                return results
    if not dry_run:
        for gone in set(offsets) - present:
            del offsets[gone]
        write_offsets(offsets_path, offsets)
    return results


def _inode_key(path: Path) -> list[str]:
    try:
        return [str(path.stat().st_ino)]
    except OSError:
        return []


def _replay_file(path: Path, signal: str, sender: Sender, offsets: dict[str, Offset], dry_run: bool) -> ReplayResult:
    try:
        inode = path.stat().st_ino
        raw = path.read_bytes()
    except OSError as exc:
        return ReplayResult(path, 0, f"{path.name}: {exc}")
    key = str(inode)
    held = offsets.get(key)
    same_file = held is not None and held.sent <= len(raw) and (not held.head or held.head == head_hash(raw, held.sent))
    start = held.sent if held is not None and same_file else 0
    end = raw.rfind(b"\n") + 1
    if end <= start:
        return ReplayResult(path, 0)
    position = start
    sent = 0
    for chunk in raw[start:end].splitlines(keepends=True):
        line = chunk.decode("utf-8", errors="replace").strip()
        if line and not dry_run:
            problem = sender.send_text(signal, line)
            if problem:
                return ReplayResult(path, sent, problem)
        position += len(chunk)
        sent += 1 if line else 0
        if not dry_run:
            offsets[key] = Offset(inode=inode, sent=position, head=head_hash(raw, position))
    return ReplayResult(path, sent)
