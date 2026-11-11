"""The routes by which a brand master can be produced.

There is no single best way to arrive at a vector mark, because the two things
you want pull against each other:

- **Fidelity** to the approved reference raster, which is what makes it *this*
  mark and not a lookalike. Tracing maximises it.
- **Geometric correctness** -- edges on the mark's own canonical axes, exact
  coordinates, no tracer stepping. Construction from named parameters
  maximises it.

Measured on rtkelly-labs: the trace scores 0.970 SSIM but only 50.1% axis
conformance with 160 micro-segments and 35 staircase runs. The parametric
construction scores 100.0% conformance with zero defects, in a quarter of the
geometry -- but nothing constrains it to resemble the reference.

So this module keeps several pathways side by side rather than picking one,
and reports both axes for each so the trade is visible instead of implied.
`autotraced` is the baseline the others must beat: a fresh, untuned VTracer
pass over the reference, scored by the same machinery.
Every pathway is deterministic: the same inputs produce byte-identical output.
"""

from __future__ import annotations

import re
import tempfile
from collections.abc import Callable, Sequence
from dataclasses import dataclass, replace
from pathlib import Path

from image_drift import labs_parametric
from image_drift.edge_regularity import DEFAULT_AXES, regularize_svg
from image_drift.labs_parametric import IsoLoopParams


@dataclass(frozen=True)
class PathwayContext:
    """What a pathway needs to build a candidate."""

    master_svg: str
    """The current governed master, for pathways that start from it."""
    reference_path: Path
    axes: tuple[float, ...] = DEFAULT_AXES
    params: IsoLoopParams | None = None
    """Starting parameters for constructive pathways."""
    score: Callable[[str], float] | None = None
    """Lower-is-better objective, for pathways that search. None disables them."""


@dataclass(frozen=True)
class Pathway:
    """One named route to a candidate mark."""

    key: str
    name: str
    description: str
    generate: Callable[[PathwayContext], str]
    needs_scoring: bool = False
    """Whether the pathway searches and therefore needs a renderer."""


def _traced(ctx: PathwayContext) -> str:
    """Pass the governed trace through unchanged."""
    return ctx.master_svg


@dataclass(frozen=True, slots=True)
class VtracerConfig:
    """VTracer settings for the `autotraced` pathway."""

    colormode: str = "color"
    hierarchical: str = "stacked"
    mode: str = "polygon"
    filter_speckle: int = 8
    color_precision: int = 8
    layer_difference: int = 10
    corner_threshold: int = 60
    length_threshold: float = 3.5
    max_iterations: int = 10
    path_precision: int = 1


_SVG_OPEN = re.compile(r"<svg\b[^>]*>")
_WIDTH = re.compile(r'\bwidth="([0-9.]+)"')
_HEIGHT = re.compile(r'\bheight="([0-9.]+)"')


def _with_viewbox(svg_text: str) -> str:
    """Drop VTracer's XML prolog and give its root a viewBox so it scales when rendered.

    VTracer writes only `width`/`height`; without a viewBox the renderer would
    crop rather than scale it onto the normalised canvas.
    """
    opening = _SVG_OPEN.search(svg_text)
    if opening is None:
        raise ValueError("VTracer output has no <svg> element")
    tag = opening.group(0)
    body = svg_text[opening.start() :]
    width, height = _WIDTH.search(tag), _HEIGHT.search(tag)
    if "viewBox=" in tag or width is None or height is None:
        return body
    patched = tag[:-1] + f' viewBox="0 0 {width.group(1)} {height.group(1)}">'
    return patched + body[len(tag) :]


def trace_reference(reference_path: Path, config: VtracerConfig | None = None) -> str:
    """Auto-trace `reference_path` with VTracer and return the SVG text. Deterministic."""
    import vtracer

    cfg = config or VtracerConfig()
    with tempfile.TemporaryDirectory(prefix="rig-autotrace-") as tmp:
        out_path = Path(tmp) / "autotraced.svg"
        vtracer.convert_image_to_svg_py(
            str(reference_path.resolve()),
            str(out_path),
            colormode=cfg.colormode,
            hierarchical=cfg.hierarchical,
            mode=cfg.mode,
            filter_speckle=cfg.filter_speckle,
            color_precision=cfg.color_precision,
            layer_difference=cfg.layer_difference,
            corner_threshold=cfg.corner_threshold,
            length_threshold=cfg.length_threshold,
            max_iterations=cfg.max_iterations,
            path_precision=cfg.path_precision,
        )
        return _with_viewbox(out_path.read_text(encoding="utf-8"))


def _autotraced(ctx: PathwayContext) -> str:
    """Trace the reference afresh with VTracer, ignoring the governed master."""
    return trace_reference(ctx.reference_path)


def _parametric(ctx: PathwayContext) -> str:
    """Construct the mark from named geometry at its default parameters."""
    return labs_parametric.generate_svg(params=ctx.params or IsoLoopParams())


def _regularized(ctx: PathwayContext) -> str:
    """Keep the trace's shapes but snap its edges onto the canonical axes."""
    return regularize_svg(ctx.master_svg, axes=ctx.axes)


@dataclass(frozen=True, slots=True)
class FitField:
    """One scalar `IsoLoopParams` field the fitter may adjust, and how to read it."""

    name: str
    read: Callable[[IsoLoopParams], float]


FIT_FIELDS: tuple[FitField, ...] = (
    FitField("r_outer", lambda p: p.r_outer),
    FitField("r_mid", lambda p: p.r_mid),
    FitField("r_inner", lambda p: p.r_inner),
    FitField("depth", lambda p: p.depth),
    FitField("beam_half_width", lambda p: p.beam_half_width),
    FitField("beam_length", lambda p: p.beam_length),
)
"""Parameters the fitter searches, in a fixed order so runs are reproducible."""

FIT_STEPS: tuple[float, ...] = (16.0, 8.0, 4.0, 2.0)
"""Step schedule. Fixed rather than adaptive, again for reproducibility."""


@dataclass(frozen=True, slots=True)
class FitResult:
    """The best parameters the fitter found, and their loss against the reference."""

    params: IsoLoopParams
    loss: float


def fit_parameters(
    ctx: PathwayContext,
    *,
    fields: Sequence[FitField] = FIT_FIELDS,
    steps: Sequence[float] = FIT_STEPS,
) -> FitResult:
    """Fit constructive parameters to the reference by coordinate descent.

    This is the bridge between the two pathways: it keeps the perfect geometry
    of construction while letting the reference decide the dimensions, rather
    than tracing the reference's pixels and inheriting its jitter.

    Deterministic by construction -- fixed field order, fixed step schedule, no
    randomness, and ties broken toward the incumbent -- so the same reference
    yields the same parameters every run.
    """
    if ctx.score is None:
        raise ValueError("fit_parameters needs ctx.score")

    best = ctx.params or IsoLoopParams()
    best_loss = ctx.score(labs_parametric.generate_svg(params=best))

    for step in steps:
        for fit_field in fields:
            current = fit_field.read(best)
            for delta in (step, -step):
                candidate_value = current + delta
                if candidate_value <= 1.0:
                    continue
                candidate = replace(best, **{fit_field.name: candidate_value})
                loss = ctx.score(labs_parametric.generate_svg(params=candidate))
                # Strict improvement only: a tie keeps the incumbent, so the
                # walk cannot oscillate between equal-scoring neighbours.
                if loss < best_loss:
                    best, best_loss = candidate, loss
    return FitResult(params=best, loss=best_loss)


def _fitted(ctx: PathwayContext) -> str:
    """Construct the mark, with its dimensions fitted to the reference."""
    return labs_parametric.generate_svg(params=fit_parameters(ctx).params)


PATHWAYS: dict[str, Pathway] = {
    "traced": Pathway(
        key="traced",
        name="Traced",
        description="The governed trace, unchanged. Highest fidelity, inherits the tracer's jitter.",
        generate=_traced,
    ),
    "autotraced": Pathway(
        key="autotraced",
        name="Autotraced (VTracer)",
        description=(
            "A fresh VTracer trace of the reference. The untuned baseline: close to the reference's pixels, "
            "worst geometry of the lot."
        ),
        generate=_autotraced,
    ),
    "regularized": Pathway(
        key="regularized",
        name="Regularized trace",
        description="The trace with its edges snapped onto the canonical axes. Hybrid.",
        generate=_regularized,
    ),
    "parametric": Pathway(
        key="parametric",
        name="Parametric",
        description="Constructed from named geometry. Exact coordinates, unconstrained resemblance.",
        generate=_parametric,
    ),
    "fitted": Pathway(
        key="fitted",
        name="Fitted parametric",
        description="Constructed from named geometry, dimensions fitted to the reference.",
        generate=_fitted,
        needs_scoring=True,
    ),
}

DEFAULT_PATHWAY = "traced"
