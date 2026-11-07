"""Shared variant-resolution contract for brand mark builder modules.

Every builder declares a `VARIANTS` registry of `Variant` records and a
`DEFAULT_VARIANT`. Before this
module existed each builder handled an unrecognised variant differently: one fell
back to its default silently, one branched on a single hardcoded key, and one
accepted the argument and discarded it entirely -- so asking for a favicon
returned the full master and the favicon was shipped as a scaled-down logo.

A silent fallback is a wrong answer wearing the shape of a right one. Resolution
therefore raises, and callers that want a default pass `None`.
"""

from __future__ import annotations

from collections.abc import Collection
from dataclasses import dataclass, field
from typing import Literal

from pydantic import StrictStr

type TilingName = Literal["rhombille", "herringbone"]
type ToneName = Literal["warm", "amber"]
type MarkName = Literal["columns", "herringbone"]


@dataclass(frozen=True, slots=True)
class StyleOverrides:
    """A variant's overrides of the builder's style defaults; None keeps the default."""

    frame_width: float | None = None
    trace_width: float | None = None
    ring_ratio: float | None = None
    node_scale: float | None = None
    glow_wide: float | None = None
    glow_tight: float | None = None
    show_emblems: bool | None = None
    trunk_only: bool | None = None
    show_circuit: bool | None = None
    show_frame: bool | None = None
    flat_faces: bool | None = None
    column_count: int | None = None
    column_segment: float | None = None
    column_gap: float | None = None
    show_footer: bool | None = None


@dataclass(frozen=True, slots=True)
class Variant:
    """One declared brand mark variant.

    `name` and `description` label it in galleries and listings. The rest only
    matter to builders that draw it parametrically: `tiling`, `cell` (tile size in
    px) and `tone` pick the wood field, `mark` the figure drawn over it, and
    `style` overrides individual entries of the builder's style defaults.
    """

    name: StrictStr = ""
    description: StrictStr = ""
    tiling: TilingName | None = None
    cell: float | None = None
    tone: ToneName | None = None
    mark: MarkName | None = None
    style: StyleOverrides = field(default_factory=StyleOverrides)


type VariantRegistry = dict[str, Variant]


class UnknownVariantError(ValueError):
    """Raised when a builder is asked for a variant it does not declare."""


def resolve_variant(variant: str | None, variants: Collection[str], default: str) -> str:
    """Resolve a requested variant key against a builder's declared variants.

    Args:
        variant: The requested key, or None to take the builder's default.
        variants: The builder's `VARIANTS` registry.
        default: The builder's `DEFAULT_VARIANT` key.

    Returns:
        The resolved variant key, guaranteed to be present in `variants`.

    Raises:
        UnknownVariantError: If `variant` is not declared, or if `default` is
            itself not declared (a builder-definition bug worth failing loudly).
    """
    chosen = default if variant is None else variant
    if chosen not in variants:
        known = ", ".join(sorted(variants))
        raise UnknownVariantError(f"Unknown variant {chosen!r}. Declared variants: {known}")
    return chosen
