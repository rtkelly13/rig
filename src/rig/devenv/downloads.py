"""One typed HTTP GET for the devenv steps that download release assets."""

from __future__ import annotations

import urllib.request
from collections.abc import Callable
from http.client import HTTPResponse

_URLOPEN: Callable[..., HTTPResponse] = urllib.request.urlopen


def fetch_bytes(url: str, *, user_agent: str, timeout: float = 30) -> bytes:
    """The body at ``url``. Refuses anything but HTTPS, since ``urlopen`` would also read ``file://`` paths.

    Raises ``ValueError`` for a non-HTTPS URL and ``OSError`` (``urllib.error.URLError``) or
    ``http.client.HTTPException`` when the fetch fails.
    """
    if not url.startswith("https://"):
        raise ValueError(f"refusing non-HTTPS URL: {url!r}")
    request = urllib.request.Request(url, headers={"User-Agent": user_agent})  # nosec B310 - https checked above
    with _URLOPEN(request, timeout=timeout) as response:
        return response.read()
