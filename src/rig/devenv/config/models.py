"""Pydantic data models for the devenv user config and git identities."""

from __future__ import annotations

from pydantic import BaseModel, Field


class GitIdentity(BaseModel):
    """Git and SSH profile identity."""

    id: str
    name: str
    email: str
    ssh_key_path: str | None = None
    signing_key: str | None = None
    match_directory: str | None = None
    is_default: bool = False


class DevenvConfig(BaseModel):
    """User/workspace-level devenv configuration."""

    identities: list[GitIdentity] = Field(default_factory=list[GitIdentity])
    custom_catalogs: list[str] = Field(default_factory=list[str])
