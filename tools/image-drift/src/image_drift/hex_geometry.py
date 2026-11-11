"""Points and the pointy-top hexagon on the 500x500 canvas.

`Point` is shared by the edge regulariser, the geometric simplifier and the
parametric labs builder; `hexagon_vertices` is the hexagon `labs_parametric`
scores against. The measured frame, tiling and circuit geometry of the
unscored hexagon explorers lives in `experiments/logo-explorers/hex_shapes.py`.
"""

from __future__ import annotations

import math
from dataclasses import dataclass


@dataclass(slots=True, frozen=True)
class Point:
    """A position, or a displacement, on the 500x500 canvas."""

    x: float
    y: float

    def offset(self, direction: Point, t: float) -> Point:
        """This point moved `t` units along `direction`."""
        return Point(self.x + direction.x * t, self.y + direction.y * t)

    def rounded(self, ndigits: int) -> Point:
        return Point(round(self.x, ndigits), round(self.y, ndigits))


HEX_CENTER = Point(250.0, 250.0)


def hexagon_vertices(radius: float, center: Point = HEX_CENTER) -> list[Point]:
    """Pointy-top hexagon vertices, starting at the top and running clockwise."""
    return [
        Point(
            round(center.x + radius * math.cos(math.radians(60 * i - 90)), 3),
            round(center.y + radius * math.sin(math.radians(60 * i - 90)), 3),
        )
        for i in range(6)
    ]
