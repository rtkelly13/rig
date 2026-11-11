"""Syntactic SVG minification: Scour scrubbing and viewBox-aware coordinate quantization.

There is no SVGO pass. It ran through an unpinned ``npx -y svgo`` and silently returned its
input when Node or the network was missing, so the same command gave different output on
different machines (ADR 0009). Everything here is pure Python and deterministic.
"""

from __future__ import annotations

from dataclasses import dataclass
from xml.dom import DOMException
from xml.etree import ElementTree
from xml.parsers.expat import ExpatError

# `simplify` can be pointed at any SVG on disk, so parsing goes through
# defusedxml: entity expansion on an SVG is a cheap denial of service.
from defusedxml.ElementTree import fromstring as _safe_fromstring
from scour import scour

from rig.image_drift.simplifier.ast_parser import (
    parse_path_d,
    serialize_path_d,
)
from rig.image_drift.simplifier.svg_attrs import path_data, root_attrs


def calculate_viewbox_precision(width: float, height: float) -> int:
    """Calculate safe coordinate precision based on viewBox dimensions.

    For small icons (e.g. 24x24), 0.1px represents 0.4% visual shift, requiring 3 decimals.
    For standard canvas (e.g. 1000x1000), 0.1px is 0.01% (imperceptible), allowing 1 decimal.
    """
    max_dim = max(width, height)
    if max_dim <= 32:
        return 3
    if max_dim <= 128:
        return 2
    return 1


@dataclass(frozen=True, slots=True)
class ViewboxSize:
    width: float
    height: float


def extract_viewbox_dimensions(root: ElementTree.Element) -> ViewboxSize:
    """Extract width and height from SVG element attributes or viewBox."""
    attrs = root_attrs(root)
    viewbox = attrs.view_box
    if viewbox:
        parts = [float(p) for p in viewbox.replace(",", " ").split() if p]
        if len(parts) >= 4:
            return ViewboxSize(parts[2], parts[3])

    w_str = attrs.width
    h_str = attrs.height
    try:
        w = float("".join(c for c in w_str if c.isdigit() or c == "."))
        h = float("".join(c for c in h_str if c.isdigit() or c == "."))
        return ViewboxSize(w, h)
    except ValueError:
        return ViewboxSize(1000.0, 1000.0)


def run_scour_pass(svg_text: str) -> str:
    """Scrub metadata, XML comments, unused defs, and empty groups via Scour."""
    options = scour.sanitizeOptions()
    options.strip_comments = True
    options.remove_metadata = True
    options.remove_descriptive_elements = True
    options.strip_xml_prolog = True
    options.remove_titles = False
    options.shorten_ids = False
    options.indent_type = "none"

    try:
        return scour.scourString(svg_text, options=options)
    except (ExpatError, SyntaxError, ValueError, ArithmeticError, DOMException):
        return svg_text


def quantize_svg_coordinates(svg_text: str, precision: int | None = None) -> str:
    """Quantize path coordinates in SVG document to scale-appropriate decimal precision."""
    try:
        ElementTree.register_namespace("", "http://www.w3.org/2000/svg")
        root = _safe_fromstring(svg_text)
    except ElementTree.ParseError:
        return svg_text

    if precision is None:
        size = extract_viewbox_dimensions(root)
        precision = calculate_viewbox_precision(size.width, size.height)

    # Walk all path elements and quantize their d attribute
    for elem in root.iter():
        original_d = path_data(elem)
        if original_d is not None:
            cmds = parse_path_d(original_d)
            if cmds:
                elem.attrib["d"] = serialize_path_d(cmds, precision=precision)

    return ElementTree.tostring(root, encoding="unicode")


def syntactic_minify_svg(
    svg_text: str,
    use_scour: bool = True,
    precision: int | None = None,
) -> str:
    """Multi-stage syntactic minification pass."""
    current = svg_text

    # 1. Scour document scrubber
    if use_scour:
        current = run_scour_pass(current)

    # 2. Scale-appropriate coordinate quantization
    return quantize_svg_coordinates(current, precision=precision)
