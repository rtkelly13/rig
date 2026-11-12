"""Typed API response models for the rig web host's Python routes.

The status, live-reload and estate answers moved to the C# host (``src/Rig.Web``, #85 s25a).
"""

from __future__ import annotations

from typing import ClassVar

from pydantic import BaseModel, ConfigDict


class ResponseModel(BaseModel):
    model_config: ClassVar[ConfigDict] = ConfigDict(frozen=True)


class BackupResponse(ResponseModel):
    status: str
    backup_file: str
