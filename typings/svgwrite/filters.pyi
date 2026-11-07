from collections.abc import Sequence

from svgwrite.base import BaseElement

class Filter(BaseElement):
    def feGaussianBlur(self, in_: str = "SourceGraphic", **extra: object) -> BaseElement: ...
    def feMerge(self, layernames: Sequence[str], **extra: object) -> BaseElement: ...
