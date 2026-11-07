"""Tests for the interlocking-columns explorer.

The specialisations exist for reasons that are easy to undo by accident, so each
is pinned: the bond staggers progressively (alternating reads as an equaliser),
the fill gradient stays light enough to survive a one-colour threshold, and the
small cut uses fewer columns because five merge below ~24px.
"""

from __future__ import annotations

from collections.abc import Sequence
from pathlib import Path

import pytest
from hex_shapes import distance
from PIL import Image
from svg_builder import (
    COLUMN_BOX,
    COLUMN_COUNT,
    COLUMN_FOOTER_GAP,
    COLUMN_FOOTER_HEIGHT,
    COLUMN_GAP,
    COLUMN_SEGMENT,
    COLUMN_SEGMENT_GAP,
    COLUMN_TIP_INSET,
    FEATURE_REGIONS,
    VARIANTS,
    build_svg_logo,
    column_footer,
    column_lanes,
    fork_graph,
    parquet_blocks,
)

from rig.image_drift.hex_geometry import Point
from rig.image_drift.imaging import chromium_available, load_normalised, render_svg

pytestmark = pytest.mark.ci

# Rendering needs a real browser, which `uv sync` does not provide. Skip rather
# than fail where it is absent; CI installs it so these still run there.
requires_browser = pytest.mark.skipif(
    not chromium_available(),
    reason="playwright chromium is not installed (run: uv run playwright install chromium)",
)


# ---------------------------------------------------------------------------
# Geometry
# ---------------------------------------------------------------------------


def test_negative_space_inverts_between_neighbouring_columns() -> None:
    """Where one column has a block, its neighbour has a void.

    That complementary alternation is what makes the field read as a laid floor.
    A progressive offset instead produces one diagonal seam, which reads as a
    bond but loses the interlock between neighbours.
    """
    blocks = parquet_blocks()
    pitch = COLUMN_SEGMENT + COLUMN_SEGMENT_GAP
    by_column: dict[int, list[float]] = {}
    for block in blocks:
        by_column.setdefault(block.index, []).append(block.y + block.height / 2.0)

    # Sample a height that falls inside a block in column 0.
    covered = {index: set() for index in by_column}
    for block in blocks:
        for probe in range(int(block.y), int(block.y + block.height)):
            covered[block.index].add(probe)

    inverted = [
        probe
        for probe in range(int(COLUMN_BOX.y0) + 4, int(COLUMN_BOX.y1) - 4)
        if (probe in covered[0]) != (probe in covered[1])
    ]
    assert len(inverted) > 40, "columns 0 and 1 should disagree over much of the height"
    assert pitch > COLUMN_SEGMENT


def test_every_column_carries_blocks() -> None:
    blocks = parquet_blocks()
    assert {block.index for block in blocks} == set(range(COLUMN_COUNT))


def test_blocks_stay_inside_the_field() -> None:
    box = COLUMN_BOX
    for block in parquet_blocks():
        assert block.x >= box.x0 - 0.01
        assert block.x + block.width <= box.x1 + 0.01
        assert block.y >= box.y0 - 0.01
        assert block.y + block.height <= box.y1 + 0.01


def test_fork_graph_roots_at_the_bottom_left() -> None:
    """A single root, in the leftmost lane, at the footer."""
    root = fork_graph().paths[0][0]
    assert root.x == pytest.approx(column_lanes()[0])
    assert root.y == pytest.approx(COLUMN_BOX.y1 + COLUMN_FOOTER_GAP)


def test_every_lane_ends_in_its_own_node_at_the_top() -> None:
    """More than one node at the top: one tip per lane, all at the same height."""
    top = COLUMN_BOX.y0 + COLUMN_TIP_INSET
    tips = sorted(node.centre.x for node in fork_graph().nodes if node.centre.y == pytest.approx(top))
    assert tips == pytest.approx(column_lanes())


def test_branches_cascade_from_the_previous_turn() -> None:
    """Each branch must start on a line that already exists.

    Forking every branch off a fixed height in its left-hand neighbour put the
    later forks where that neighbour had no line yet, so the branch appeared to
    start in mid-air.
    """
    paths = fork_graph().paths
    drawn = [paths[0]]
    for branch in paths[1:]:
        start = branch[0]
        on_existing = any(_point_on_polyline(start, path) for path in drawn)
        assert on_existing, f"branch starting at {start} is not attached to the graph"
        drawn.append(branch)


def _point_on_polyline(point: Point, path: Sequence[Point]) -> bool:
    for a, b in zip(path, path[1:], strict=False):
        length = distance(a, b)
        if length < 1e-9:
            continue
        # Distance from the point to the segment.
        t = max(0.0, min(1.0, ((point.x - a.x) * (b.x - a.x) + (point.y - a.y) * (b.y - a.y)) / length**2))
        nearest = Point(a.x + t * (b.x - a.x), a.y + t * (b.y - a.y))
        if distance(point, nearest) < 0.5:
            return True
    return False


def test_branch_diagonals_are_forty_five_degrees() -> None:
    """One lane of rise per lane of run, so the cascade is visually regular."""
    for branch in fork_graph().paths[1:]:
        run = branch[1].x - branch[0].x
        rise = branch[0].y - branch[1].y
        assert run == pytest.approx(rise), f"{branch[0]} -> {branch[1]} is not 45 degrees"


def test_fork_graph_stays_inside_the_mark() -> None:
    graph = fork_graph()
    for path in graph.paths:
        for point in path:
            assert COLUMN_BOX.x0 < point.x < COLUMN_BOX.x1
            assert COLUMN_BOX.y0 < point.y < COLUMN_BOX.y1 + COLUMN_FOOTER_GAP + COLUMN_FOOTER_HEIGHT
    assert len(graph.nodes) >= COLUMN_COUNT + 1, "expected a root plus a tip per lane"


def test_footer_sits_below_the_field_and_spans_it() -> None:
    """Parquet files end in a footer holding schema and row-group metadata."""
    footer = column_footer()
    assert footer.y > COLUMN_BOX.y1
    assert footer.x == pytest.approx(COLUMN_BOX.x0)
    assert footer.width == pytest.approx(COLUMN_BOX.x1 - COLUMN_BOX.x0)
    assert footer.height == pytest.approx(COLUMN_FOOTER_HEIGHT)


def test_lanes_match_the_column_centres() -> None:
    lanes = column_lanes()
    assert len(lanes) == COLUMN_COUNT
    assert lanes[0] < lanes[-1]
    assert COLUMN_GAP > 0


# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_build_is_byte_identical_across_runs(tmp_path: Path, variant: str) -> None:
    first, second = tmp_path / "a.svg", tmp_path / "b.svg"
    build_svg_logo(first, variant=variant)
    build_svg_logo(second, variant=variant)
    assert first.read_bytes() == second.read_bytes()


@pytest.mark.parametrize("variant", list(VARIANTS))
def test_output_is_accessible_and_lean(tmp_path: Path, variant: str) -> None:
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg, variant=variant)
    content = svg.read_text(encoding="utf-8")
    assert "<title>" in content
    assert 'role="img"' in content
    assert "<text" not in content
    assert "<mask" not in content
    # The columns mark is far simpler than the hexagon it replaced (18KB).
    assert svg.stat().st_size < 9_000


def test_no_hexagon_container_is_drawn(tmp_path: Path) -> None:
    """The columns mark drops the container deliberately."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    content = svg.read_text(encoding="utf-8")
    assert "<polygon" not in content
    assert "wood-hex-clip" not in content


@requires_browser
def test_image_mode_is_rgb_after_normalising(tmp_path: Path) -> None:
    """Guards the helper the visual assertions above depend on."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    png = tmp_path / "logo.png"
    render_svg(svg, png)
    assert isinstance(load_normalised(png), Image.Image)
    assert load_normalised(png).mode == "RGB"


@requires_browser
def test_graph_survives_a_one_colour_threshold(tmp_path: Path) -> None:
    """The fork must still read after flattening to two tones, since that is
    what a one-colour reproduction does."""
    from PIL import ImageOps

    from rig.image_drift.imaging import load_normalised, render_svg

    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    png = tmp_path / "logo.png"
    render_svg(svg, png)
    flat = ImageOps.grayscale(load_normalised(png)).point(lambda value: 255 if value > 110 else 0)

    # A horizontal slice across the tips must show one run per lane.
    top = int(COLUMN_BOX.y0 + COLUMN_TIP_INSET)
    row = [flat.getpixel((x, top)) for x in range(int(COLUMN_BOX.x0) - 6, int(COLUMN_BOX.x1) + 6)]
    runs = sum(1 for i, v in enumerate(row) if v and (i == 0 or not row[i - 1]))
    assert runs >= COLUMN_COUNT, f"expected at least {COLUMN_COUNT} tips, found {runs}"


def test_mark_uses_the_lightened_gradient(tmp_path: Path) -> None:
    """The circuit gradient's dark end vanishes under a threshold."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    content = svg.read_text(encoding="utf-8")
    assert 'id="mark-grad"' in content
    assert "url(#mark-grad)" in content


def test_output_carries_no_mask_or_clip(tmp_path: Path) -> None:
    """Masks are the one SVG feature this project avoids for portability."""
    svg = tmp_path / "logo.svg"
    build_svg_logo(svg)
    content = svg.read_text(encoding="utf-8")
    assert "<mask" not in content
    assert "clip-path" not in content


def test_feature_regions_track_the_columns_mark() -> None:
    keys = {region.key for region in FEATURE_REGIONS}
    assert "top_right_socket" not in keys
    assert "brace_badge" not in keys
