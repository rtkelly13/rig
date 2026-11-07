from svgwrite.base import BaseElement

class Group(BaseElement): ...
class Defs(Group): ...

class SVG(BaseElement):
    defs: Defs
