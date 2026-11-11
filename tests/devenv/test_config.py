"""Tests for the devenv user config loader."""

from pathlib import Path

import pytest

from rig.devenv.config.loader import load_user_config, save_user_config
from rig.devenv.config.models import DevenvConfig, GitIdentity


@pytest.mark.ci
def test_load_and_save_user_config(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    fake_config_path = tmp_path / "devenv" / "config.json"
    monkeypatch.setattr("rig.devenv.config.loader.CONFIG_PATH", fake_config_path)

    # Missing file returns default config
    cfg = load_user_config()
    assert isinstance(cfg, DevenvConfig)
    assert len(cfg.identities) == 0

    # Save and reload
    cfg.custom_catalogs = ["~/catalogs/extra.json"]
    cfg.identities.append(
        GitIdentity(
            id="personal",
            name="Developer",
            email="dev@example.com",
            is_default=True,
        )
    )
    save_user_config(cfg)

    reloaded = load_user_config()
    assert reloaded.custom_catalogs == ["~/catalogs/extra.json"]
    assert len(reloaded.identities) == 1
    assert reloaded.identities[0].email == "dev@example.com"
