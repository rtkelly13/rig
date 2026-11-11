"""Tests for SVG Path AST Parser and Tokenizer."""

from __future__ import annotations

import pytest

from image_drift.simplifier.ast_parser import (
    PathCommand,
    SVGPathTokenizer,
    parse_path_d,
    serialize_path_d,
)

pytestmark = pytest.mark.ci


def test_basic_path_tokenization() -> None:
    d = "M 10 20 L 30 40 Z"
    commands = parse_path_d(d)
    assert len(commands) == 3
    assert commands[0] == PathCommand("M", [10.0, 20.0])
    assert commands[1] == PathCommand("L", [30.0, 40.0])
    assert commands[2] == PathCommand("Z", [])


def test_compact_number_tokenization() -> None:
    # Compact notation: sign delimiters, scientific notation, leading-zero omission
    d = "M10-20.5L.5.25e2Z"
    commands = parse_path_d(d)
    assert len(commands) == 3
    assert commands[0] == PathCommand("M", [10.0, -20.5])
    assert commands[1] == PathCommand("L", [0.5, 25.0])
    assert commands[2] == PathCommand("Z", [])


def test_arc_command_preservation() -> None:
    # Arc command: rx ry x-axis-rotation large-arc sweep x y
    d = "M 100 100 A 25 25 -30 0 1 150 120 Z"
    commands = parse_path_d(d)
    assert len(commands) == 3
    arc = commands[1]
    assert arc.command == "A"
    assert arc.args == [25.0, 25.0, -30.0, 0.0, 1.0, 150.0, 120.0]


def test_cubic_bezier_command() -> None:
    d = "M 0 0 C 10 20, 30 40, 50 60 Z"
    commands = parse_path_d(d)
    assert len(commands) == 3
    assert commands[1] == PathCommand("C", [10.0, 20.0, 30.0, 40.0, 50.0, 60.0])


def test_subpath_decomposition() -> None:
    # Compound path: outer box + inner hole
    d = "M 0 0 L 100 0 L 100 100 L 0 100 Z M 20 20 L 80 20 L 80 80 L 20 80 Z"
    subpaths = SVGPathTokenizer.extract_subpaths(d)
    assert len(subpaths) == 2
    assert subpaths[0].is_closed is True
    assert len(subpaths[0].commands) == 5
    assert subpaths[1].is_closed is True
    assert len(subpaths[1].commands) == 5


def test_open_stroke_detection() -> None:
    d = "M 0 0 L 50 50 L 100 0"
    subpaths = SVGPathTokenizer.extract_subpaths(d)
    assert len(subpaths) == 1
    assert subpaths[0].is_closed is False


def test_serialization_precision() -> None:
    commands = [
        PathCommand("M", [10.12345, 20.6789]),
        PathCommand("L", [30.0000, 40.5000]),
        PathCommand("Z", []),
    ]
    serialized = serialize_path_d(commands, precision=1)
    assert serialized == "M 10.1 20.7 L 30 40.5 Z"
