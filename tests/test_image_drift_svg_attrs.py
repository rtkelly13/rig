from __future__ import annotations

import pytest
from defusedxml.ElementTree import fromstring

from rig.image_drift.simplifier.svg_attrs import SvgRootAttrs, path_data, root_attrs

pytestmark = pytest.mark.ci

_SVG = (
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24px">'
    '<path d="M0 0L1 1"/><path fill="red"/><rect d="ignored"/></svg>'
)


def test_root_attrs_reads_sizing_and_falls_back_to_1000() -> None:
    assert root_attrs(fromstring(_SVG)) == SvgRootAttrs(view_box="0 0 24 24", width="24px", height="1000")


def test_path_data_is_only_the_d_of_path_elements() -> None:
    root = fromstring(_SVG)
    assert [path_data(elem) for elem in root.iter()] == [None, "M0 0L1 1", None, None]
