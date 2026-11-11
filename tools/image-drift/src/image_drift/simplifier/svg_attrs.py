"""Typed views of the SVG attributes the simplifier reads."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Annotated
from xml.etree import ElementTree

from pydantic import Field

from image_drift.typed_json import decode


@dataclass(frozen=True, slots=True)
class SvgRootAttrs:
    view_box: Annotated[str, Field(alias="viewBox")] = ""
    width: str = "1000"
    height: str = "1000"


@dataclass(frozen=True, slots=True)
class _PathAttrs:
    d: str | None = None


def root_attrs(root: ElementTree.Element) -> SvgRootAttrs:
    """The sizing attributes of an `<svg>` root, with the simplifier's 1000x1000 fallback."""
    return decode(SvgRootAttrs, root.attrib)


def path_data(elem: ElementTree.Element) -> str | None:
    """The `d` of a `<path>` element; None for any other element or a path without one."""
    if not elem.tag.endswith("path"):
        return None
    return decode(_PathAttrs, elem.attrib).d
