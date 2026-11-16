"""What Python still reads of the estate's state: Orca's profile folder."""

from __future__ import annotations

import pathlib

from rig.estate import orca_runtime


def test_the_estate_orca_profile_sits_beside_or_is_the_default_one() -> None:
    default = orca_runtime.default_user_data()
    estate = orca_runtime.estate_user_data()

    assert default.is_relative_to(pathlib.Path.home())
    assert estate.parent == default.parent
    assert estate.name in {default.name, orca_runtime.ESTATE_PROFILE_FOLDER}
