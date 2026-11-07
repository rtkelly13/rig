"""Pydantic data models for tools, workspace config, and identities."""

from __future__ import annotations

from pydantic import BaseModel, Field


class ToolInstallSpec(BaseModel):
    """Platform installation commands and package identifiers."""

    winget: str | None = None
    brew: str | None = None
    check_cmd: str | None = None


class ToolDefinition(BaseModel):
    """Tool metadata and installation specification."""

    id: str
    name: str
    description: str
    category: str = "utilities"
    install: ToolInstallSpec = Field(default_factory=ToolInstallSpec)
    required: bool = False


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
