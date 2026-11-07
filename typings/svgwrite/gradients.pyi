from svgwrite.base import BaseElement

class LinearGradient(BaseElement):
    def add_stop_color(
        self, offset: str | float | None = None, color: str | None = None, opacity: float | None = None
    ) -> LinearGradient: ...
