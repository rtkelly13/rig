# Drawing the estate graph

**Code:** [`web/estateEdges.ts`](../../web/estateEdges.ts) (the router) ·
[`web/Estate.tsx`](../../web/Estate.tsx) (the page) ·
**Test:** `test_estate_lines_turn_in_gutters_and_never_cross_a_box` in
[`tests/test_web_spa.py`](../../tests/test_web_spa.py) ·
**Generic version:** the vault's `graph-layout` skill (`obsidian-vault/.agents/skills/graph-layout/`)

The estate page draws the repos as a dependency graph. The boxes are placed by the
design system's `layoutServiceGraph`: a layered, left-to-right layout over a graph whose
cycles have already been folded into one box each, so every edge runs forward.
Rig keeps that placement. It draws the lines itself.

These rules come from an earlier schema visualiser, where most of them were learned by
getting them wrong first.

## Why the layout's own lines were replaced

`layoutServiceGraph` draws each edge as an S-curve from the middle of the source's right
side to the middle of the target's left side. On a graph of a dozen or more repos, two
things go wrong:

- **One port per side.** Every line out of a box leaves from the same point, so five
  consumers read as one thick line until they fan out, and they cross where they fan out.
- **Long edges go through boxes.** An edge that skips a layer curves across the layers in
  between, through whatever box is standing there.

## The rules

| rule | why |
|---|---|
| Lines leave a right side and arrive at a left side, never top or bottom | in a layered layout the side follows the rank direction. Mixing sides turns the picture into a plaid |
| Ports are spread over the middle 60% of a side, ordered by the y of the line's other end | lines leave in the order they are going, so they do not cross on the way out, and none of them seems to meet a corner |
| Right angles, with every vertical run (a *trunk*) in the gutter between two layers | a trunk inside a layer runs through a column of boxes. The gutter is the space the layout left for turning |
| Trunks in one gutter whose runs overlap get separate lanes, spread around the gutter's centre (`LANE` = 10, closer if the gutter is full) | two overlapping trunks on one x read as a single line |
| An edge that skips layers crosses them along a *channel*: a y where no box in the skipped layers stands, with 6px clearance | no line passes through a box. The router tries the source port's y first (one bend fewer), then the target's, then the clear band nearest the middle |
| Two long lines never share a channel over the same stretch | they would merge into one line |
| Square joins (`stroke-linejoin: miter`), no rounded corners | a rounded corner reads as a curve that changed its mind |
| `vector-effect: non-scaling-stroke` on the lines | the page zooms with a CSS `scale()`. Without this, lines go hairline when zoomed out and fat when zoomed in |
| The arrowhead is `markerUnits="userSpaceOnUse"`, ending exactly on the border (`refX` = the tip) | with the default `strokeWidth` units, the head scales with the stroke. With the tip on the border, the line meets the box and does not stop short |

The spacing is in `SPACING` in `Estate.tsx` (`columnGap` 120, `rowGap` 36). The layout and
the router both read it. The gutter has to hold several lanes plus the arrowhead, so a
narrower `columnGap` squeezes the lane pitch before anything else.

## What the test checks

The browser test builds a five-repo graph containing a fan-out, a fan-in and a line that
skips a layer. It walks every drawn path with `getPointAtLength`, at zoom 1 and again
zoomed out:

- no sampled point is inside a box other than the line's own two ends;
- every segment is horizontal or vertical;
- each line starts and ends within 2.5px of its boxes' borders;
- the stroke is non-scaling;
- ports on a shared side are at least 3px apart.

These are structural checks, not pixel snapshots, so a change to the spacing does not
break them, while a regression to the old curves does. Swapping in `serviceEdgePath`
makes the test fail on the first assertion.

## Not done (yet)

- **Edge bundling.** Rig draws one line per dependency. If the estate ever has parallel
  edges, the usual approach is to bundle them, with a logarithmic stroke width
  `min(1.2 + log2(n)·0.9, 7)` and an "N references" label.
- **Crossing reduction across layers.** Ports and lanes are ordered locally. Box order
  within a layer is still the layout's own barycentre pass.
- **Back edges.** These shouldn't exist after SCC folding. If one does, it keeps the
  layout's loop under the graph.
