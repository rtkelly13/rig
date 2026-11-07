"""Compatibility forwarding layer: ``shared_utilities`` -> ``rig``."""

from __future__ import annotations

import sys
from pathlib import Path

import rig

sys.modules["shared_utilities"] = rig
if rig.__file__ is not None:
    __path__ = [str(Path(rig.__file__).parent)]
