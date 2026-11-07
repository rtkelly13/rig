from collections.abc import Iterable, Sequence

from svgwrite.base import BaseElement
from svgwrite.container import SVG
from svgwrite.filters import Filter
from svgwrite.gradients import LinearGradient
from svgwrite.masking import ClipPath

type _Number = float | str
type _Pair = Sequence[_Number]

class Drawing(SVG):
    filename: str
    def __init__(self, filename: str = "noname.svg", size: _Pair = ..., **extra: object) -> None: ...
    def save(self, pretty: bool = False, indent: int = 2) -> None: ...
    def saveas(self, filename: str, pretty: bool = False, indent: int = 2) -> None: ...
    def rect(
        self,
        insert: _Pair = ...,
        size: _Pair = ...,
        rx: _Number | None = None,
        ry: _Number | None = None,
        **extra: object,
    ) -> BaseElement: ...
    def circle(self, center: _Pair = ..., r: _Number = 1, **extra: object) -> BaseElement: ...
    def polygon(self, points: Iterable[_Pair] = ..., **extra: object) -> BaseElement: ...
    def polyline(self, points: Iterable[_Pair] = ..., **extra: object) -> BaseElement: ...
    def path(self, d: str | None = None, **extra: object) -> BaseElement: ...
    def linearGradient(
        self, start: _Pair | None = None, end: _Pair | None = None, inherit: str | None = None, **extra: object
    ) -> LinearGradient: ...
    def clipPath(self, **extra: object) -> ClipPath: ...
    def filter(
        self,
        start: _Pair | None = None,
        size: _Pair | None = None,
        resolution: object = None,
        inherit: object = None,
        **extra: object,
    ) -> Filter: ...
