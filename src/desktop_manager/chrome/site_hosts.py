"""Site groups by host, from `config/chrome-site-groups.json`.

A tab whose title names no repo or site still has a host: its URL's where the
driver reports one (macOS), else the one history ties its title to (Windows).
This maps that host to a group by domain, so `docs.renovatebot.com` joins Dev
and `chatgpt.com` joins AI. A host matches a domain it equals or ends with
(`www.npmjs.com` matches `npmjs.com`), and the longest matching domain wins, so
`gemini.google.com` is AI while `docs.google.com` stays Google.

Call hosts are not here: they never group, and that is a safety rule kept in code.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from functools import cache
from pathlib import Path

from pydantic import StrictStr

from rig.typed_json import decode_json

CONFIG_NAME = "chrome-site-groups.json"
_DOMAIN = re.compile(r"^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$")


@dataclass(frozen=True, slots=True)
class SiteDomain:
    domain: str
    site: str


@dataclass(frozen=True, slots=True)
class SiteHosts:
    domains: tuple[SiteDomain, ...] = ()

    def site_for(self, host: str) -> str | None:
        name = host.lower()
        best: str | None = None
        best_length = 0
        for entry in self.domains:
            domain = entry.domain
            if (name == domain or name.endswith("." + domain)) and len(domain) > best_length:
                best, best_length = entry.site, len(domain)
        return best


@dataclass(frozen=True, slots=True)
class _Config:
    groups: dict[StrictStr, tuple[StrictStr, ...]]


def load_site_hosts(path: Path) -> SiteHosts:
    """The configured groups; a malformed or twice-listed domain raises ``ValueError``."""
    config = decode_json(_Config, path.read_text(encoding="utf-8"))
    owners: dict[str, str] = {}
    for site, domains in config.groups.items():
        if not site.strip():
            raise ValueError("a site group needs a title")
        for domain in domains:
            if not _DOMAIN.match(domain):
                raise ValueError(f"{site}: {domain!r} is not a lowercase domain")
            if domain in owners:
                raise ValueError(f"{site}: {domain} is already in {owners[domain]}")
            owners[domain] = site
    return SiteHosts(tuple(SiteDomain(domain, site) for domain, site in owners.items()))


PACKAGED_CONFIG = Path(__file__).with_name(CONFIG_NAME)


def default_config_path() -> Path | None:
    """The checkout's config beside the managed-projects registry, else the wheel's copy.

    The snapshot `chrome-tabs install` builds has no checkout, so the wheel
    carries the config beside this module (`force-include` in pyproject.toml)."""
    from rig.workspace.engine import config_file_path

    for path in (config_file_path().parent / CONFIG_NAME, PACKAGED_CONFIG):
        if path.is_file():
            return path
    return None


@cache
def default_site_hosts() -> SiteHosts:
    """The configured groups; none when neither config is there."""
    path = default_config_path()
    return load_site_hosts(path) if path else SiteHosts()
