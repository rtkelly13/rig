"""The managed tree walk `du` falls back to where there is no `du` (Windows): hardlinks counted once."""

from __future__ import annotations

import os
import pathlib

import pytest

from rig.estate import disksize


@pytest.mark.ci
def test_measure_counts_hardlink_only_when_every_link_is_inside(tmp_path: pathlib.Path) -> None:
    tree = tmp_path / "venv"
    tree.mkdir()
    (tree / "own.bin").write_bytes(b"x" * 10_000)
    cache = tmp_path / "cache"
    cache.mkdir()
    shared = cache / "shared.bin"
    shared.write_bytes(b"y" * 50_000)
    os.link(shared, tree / "shared.bin")
    inside = tree / "twin-a.bin"
    inside.write_bytes(b"z" * 20_000)
    os.link(inside, tree / "twin-b.bin")

    size = disksize.measure_windows(tree)

    # own.bin, plus the twin pair once; shared.bin stays on disk via the cache.
    assert size.freeable == disksize._allocated(10_000) + disksize._allocated(20_000)
    assert size.shared == disksize._allocated(50_000)
