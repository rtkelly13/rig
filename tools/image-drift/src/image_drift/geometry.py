"""Pixel-space boxes and arrays shared by the scoring, cropping and contact-sheet code."""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np
import numpy.typing as npt
from PIL import Image

type FloatArray = npt.NDArray[np.floating]


@dataclass(frozen=True, slots=True)
class CropBox:
    """A PIL crop box on the normalised canvas: left/top inclusive, right/bottom exclusive."""

    left: int
    top: int
    right: int
    bottom: int

    def crop(self, image: Image.Image) -> Image.Image:
        return image.crop((self.left, self.top, self.right, self.bottom))
