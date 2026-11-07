from __future__ import annotations

import logging
import re
import unicodedata
import urllib.parse
from dataclasses import dataclass
from pathlib import Path
from typing import TYPE_CHECKING

import yaml

if TYPE_CHECKING:
    import frontmatter


logger = logging.getLogger(__name__)

#: What reading or writing a note's frontmatter can raise: file I/O, undecodable text, malformed YAML,
#: or YAML that does not load as a mapping.
FRONTMATTER_ERRORS: tuple[type[Exception], ...] = (OSError, ValueError, TypeError, yaml.YAMLError)


def clean_ws(s: str) -> str:
    """Normalize whitespace and strip."""
    return re.sub(r"\s+", " ", (s or "")).strip()


def normalize_url(url: str) -> str:
    """Strip fragments and normalize URL for stable hashing."""
    if not url:
        return ""

    # Remove tracking parameters
    url = clean_url(url.strip())

    u = urllib.parse.urlsplit(url)
    # Lowercase the host and remove 'www.'
    host = (u.hostname or "").lower()
    if host.startswith("www."):
        host = host[4:]

    # Strip fragments and trailing slash from path
    path = u.path.rstrip("/")

    # Reconstruct normalized URL
    norm = urllib.parse.urlunsplit((u.scheme.lower(), host, path, u.query, ""))

    # Normalize X/Twitter URLs (strip the username, convert to /i/status/)
    # Matches x.com/username/status/123 or twitter.com/username/status/123
    norm = re.sub(r"https?://(?:x|twitter)\.com/[^/]+/status/(\d+)", r"https://x.com/i/status/\1", norm, flags=re.I)

    return norm


def is_http_url(url: str) -> bool:
    """Check if URL is HTTP or HTTPS."""
    try:
        s = urllib.parse.urlsplit(url)
    except ValueError:
        return False
    return s.scheme in {"http", "https"}


def clean_url(url: str) -> str:
    """Remove tracking parameters from a URL."""
    u = urllib.parse.urlsplit(url)
    q = urllib.parse.parse_qs(u.query)
    # List of common tracking parameters
    tracking_params = {
        "utm_source",
        "utm_medium",
        "utm_campaign",
        "utm_term",
        "utm_content",
        "ref",
        "ref_src",
        "ref_url",
        "s",
        "t",
        "fbclid",
        "gclid",
        "msclkid",
        "mc_cid",
        "mc_eid",
        "oai-dm",
        "source",
    }
    # Keep only non-tracking parameters
    new_q = {k: v for k, v in q.items() if k.lower() not in tracking_params}
    new_query = urllib.parse.urlencode(new_q, doseq=True)
    return urllib.parse.urlunsplit(u._replace(query=new_query))


def sanitize_filename(stem: str, *, max_len: int = 120) -> str:
    """Create a cross-platform safe filename stem."""
    s = (stem or "").strip()
    s = re.sub(r"[\\/:*?\"<>|]", "-", s)
    s = re.sub(r"\s+", " ", s)
    s = s.strip(" .")
    s = re.sub(r"\.{2,}", ".", s)
    s = s.replace(".", "-")
    s = re.sub(r"-{2,}", "-", s).strip("-")
    if not s:
        s = "Untitled"
    if len(s) > max_len:
        s = s[:max_len].rstrip(" .-")
    return s


def dump_frontmatter(post: frontmatter.Post) -> str:
    """Serialize frontmatter while forcing URL-like source values to be quoted."""
    import frontmatter

    text = frontmatter.dumps(post)
    return re.sub(r"(?m)^source:\s+(https?://\S+)$", lambda m: f'source: "{m.group(1)}"', text)


def looks_like_url(value: str) -> bool:
    """Return True when a string appears to be a URL or domain-style label."""
    s = clean_ws(value)
    if not s:
        return False
    if is_http_url(s):
        return True
    return bool(
        re.match(
            r"^(?:www\.)?[a-z0-9-]+(?:\.[a-z0-9-]+)+(?:[/?#].*)?$",
            s.lower(),
        )
    )


def looks_like_urlish_stem(value: str) -> bool:
    """Return True when a filename stem still looks like a URL after sanitization."""
    s = clean_ws(value).strip("-_. ").lower()
    if not s:
        return False
    if is_http_url(s) or s.startswith("www."):
        return True
    return any(
        [
            bool(re.search(r"(?:^|[-_. ])https?(?:[-_. ]|$)", s)),
            bool(re.search(r"(?:^|[-_. ])www(?:[-_. ]|$)", s)),
            bool(
                re.search(
                    r"(?:^|[^a-z0-9])(?:[a-z0-9-]+(?:\.[a-z0-9-]+)+)(?:$|[^a-z0-9])",
                    s,
                )
            ),
        ]
    )


def slugify_note_stem(value: str, *, max_len: int = 80) -> str:
    """Convert a note title or URL fragment into a compact ASCII filename stem."""
    s = clean_ws(value)
    if not s:
        return "untitled"
    s = s.replace("&", " and ").replace("_", " ")
    s = unicodedata.normalize("NFKD", s)
    s = s.encode("ascii", "ignore").decode("ascii")
    s = re.sub(r"['’]", "", s)
    s = re.sub(r"[^A-Za-z0-9]+", "-", s)
    s = re.sub(r"-{2,}", "-", s).strip("-").lower()
    if not s:
        return "untitled"
    if len(s) > max_len:
        s = s[:max_len].rstrip("-")
    return s or "untitled"


def _extract_related_article_stem(value: str) -> str:
    match = re.search(r"\[\[([^\]|]+)", value or "")
    if not match:
        return ""
    raw = match.group(1).split("/")[-1]
    return raw.removesuffix(".md").strip()


def _derive_stem_from_url(url: str) -> str:
    cleaned = clean_ws(url)
    if not cleaned:
        return "untitled"

    split = urllib.parse.urlsplit(cleaned)
    host = (split.hostname or "").lower()
    if host.startswith("www."):
        host = host[4:]

    parts = [urllib.parse.unquote(part) for part in split.path.split("/") if part]
    parts = [re.sub(r"\.[A-Za-z0-9]{1,8}$", "", part) for part in parts]

    if host in {"x.com", "twitter.com"}:
        if len(parts) >= 3 and parts[1].lower() == "status":
            return slugify_note_stem(f"{parts[0]} {parts[2]}")
        if parts and parts[0].lower() != "i":
            return slugify_note_stem(parts[0])

    for part in reversed(parts):
        if part.lower() not in {"index", "home", "default", "index.html"}:
            return slugify_note_stem(part)

    host_label = host.split(".")[0] if host else ""
    return slugify_note_stem(host_label or cleaned)


def _is_bad_slug_candidate(slug: str, *, source_url: str = "") -> bool:
    candidate = slugify_note_stem(slug)
    if not candidate or candidate == "untitled":
        return True

    generic_slugs = {
        "app",
        "apps",
        "article",
        "articles",
        "blog",
        "box",
        "default",
        "docs",
        "document",
        "edit",
        "forum",
        "google",
        "home",
        "index",
        "new",
        "post",
        "posts",
        "resource",
        "showpost",
        "showthread",
        "spreadsheets",
        "thread",
        "video",
        "watch",
    }
    if candidate in generic_slugs:
        return True
    if len(candidate) <= 3 and not re.search(r"\d", candidate):
        return True
    if re.fullmatch(r"[a-z0-9]{10,}", candidate):
        return True

    host = urllib.parse.urlsplit(source_url).hostname or ""
    host = host.lower().removeprefix("www.")
    host_specific: dict[str, set[str]] = {
        "docs.google.com": {"edit", "view", "preview", "copy", "export", "spreadsheets", "document"},
        "docs.googleusercontent.com": {"edit", "view", "preview"},
        "youtube.com": {"watch", "channel", "playlist", "live"},
        "youtu.be": {"watch"},
        "box.com": {"s", "file", "folder"},
        "app.box.com": {"s", "file", "folder"},
        "xda-developers.com": {"showpost", "showthread", "forums", "forum"},
        "forum.xda-developers.com": {"showpost", "showthread", "forums", "forum"},
        "diagrams.net": {"app"},
        "app.diagrams.net": {"app"},
    }
    return candidate in host_specific.get(host, set())


def _candidate_quality(slug: str, *, source_url: str = "", from_url: bool = False) -> int:
    candidate = slugify_note_stem(slug)
    if not candidate or candidate == "untitled":
        return -100

    score = 0
    length = len(candidate)
    score += min(length, 60)
    score += candidate.count("-") * 2
    if re.search(r"[a-z]{4,}", candidate):
        score += 10
    if re.search(r"\d", candidate):
        score += 2
    if _is_bad_slug_candidate(candidate, source_url=source_url):
        score -= 80
    if from_url:
        score -= 8
    return score


def _strip_source_host_prefix(title: str, source_url: str) -> str:
    cleaned_title = clean_ws(title)
    if not cleaned_title or not source_url:
        return cleaned_title

    host = urllib.parse.urlsplit(source_url).hostname or ""
    host = host.lower().removeprefix("www.")
    if not host:
        return cleaned_title

    prefixes = [host]
    first_label = host.split(".")[0]
    if first_label and first_label != host:
        prefixes.append(first_label)

    updated = cleaned_title
    for prefix in prefixes:
        pattern = re.compile(rf"(?i)^{re.escape(prefix)}[.\-:\s]+")
        updated = pattern.sub("", updated).strip()
        if updated != cleaned_title:
            return updated

    return cleaned_title


def _strip_host_brand_suffix(title: str, source_url: str) -> str:
    cleaned_title = clean_ws(title)
    if not cleaned_title or not source_url:
        return cleaned_title

    host = urllib.parse.urlsplit(source_url).hostname or ""
    host = host.lower().removeprefix("www.")
    if not host:
        return cleaned_title

    suffixes = [host, host.split(".")[0]]
    suffixes.extend(
        part
        for part in host.split(".")
        if part
        not in {"com", "org", "net", "io", "dev", "ai", "app", "co", "uk", "eu", "fm", "me", "tv", "ly", "sh", "rs"}
    )
    updated = cleaned_title
    for suffix in suffixes:
        suffix = suffix.strip()
        if not suffix:
            continue
        pattern = re.compile(rf"(?i)[\s\-:|()]+{re.escape(suffix)}$")
        candidate = pattern.sub("", updated).strip()
        if candidate and candidate != updated:
            updated = candidate
    return updated


def _expand_common_stem_terms(text: str) -> str:
    expanded = clean_ws(text)
    replacements = {
        ".net": " dotnet ",
        "asp.net": " asp dotnet ",
        "node.js": " nodejs ",
        "next.js": " nextjs ",
        "gatsby.js": " gatsbyjs ",
        "knockout.js": " knockoutjs ",
        "moment.js": " momentjs ",
        "draw.io": " draw io ",
        "json-generator.com": " json generator ",
        "plex.tv": " plex ",
        "codeproject": " code project ",
        "stack overflow": " stack overflow ",
        "youtube": " youtube ",
    }
    lowered = expanded.lower()
    for old, new in replacements.items():
        lowered = lowered.replace(old, new)
    return clean_ws(lowered)


def _remove_domain_like_lead(value: str) -> str:
    cleaned = clean_ws(value)
    patterns = [
        r"^(?:[a-z0-9-]+(?:\.[a-z0-9-]+)+)[.\-:\s]+",
        r"^(?:[a-z0-9-]+\s+)+(?:com|org|net|io|dev|ai|app|co|uk|eu|fm|me|tv|ly|sh|rs)[.\-:\s]+",
    ]
    updated = cleaned
    for pattern in patterns:
        updated = re.sub(pattern, "", updated, flags=re.I).strip()
    return updated


def _normalize_current_stem(value: str) -> str:
    expanded = _expand_common_stem_terms(value)
    expanded = expanded.replace(".", " ")
    return clean_ws(expanded)


def _derive_stem_from_urlish_stem(stem: str) -> str:
    normalized = clean_ws(stem).strip("-_. ")
    if not normalized:
        return ""

    social_suffix = ""
    if normalized.lower().endswith("-social"):
        social_suffix = "-social"
        normalized = normalized[: -len(social_suffix)].rstrip("-_. ")

    tokenized = re.sub(r"[_.]+", "-", normalized).strip("-").lower()
    if not tokenized:
        return ""

    if not looks_like_urlish_stem(normalized):
        return ""

    parts = [part for part in re.split(r"-+", tokenized) if part]
    if not parts:
        return ""

    if parts[0] in {"http", "https"}:
        parts = parts[1:]
    if parts[:1] == ["www"]:
        parts = parts[1:]
    if not parts:
        return ""

    tlds = {"com", "org", "net", "io", "dev", "ai", "app", "co", "uk", "eu", "fm", "me", "tv", "ly", "sh", "rs"}
    host_end = -1
    for idx, part in enumerate(parts):
        if part in tlds:
            host_end = idx
            if idx + 1 < len(parts) and parts[idx + 1] in tlds:
                host_end = idx + 1
            break

    if host_end >= 0:
        host_parts = parts[: host_end + 1]
        rest_parts = parts[host_end + 1 :]
        host = ".".join(host_parts)
        if host in {"x.com", "twitter.com"} and rest_parts:
            return slugify_note_stem("-".join(rest_parts)) + social_suffix
        if rest_parts:
            return slugify_note_stem("-".join(rest_parts)) + social_suffix
        return slugify_note_stem(host_parts[0]) + social_suffix

    return ""


@dataclass(frozen=True, slots=True)
class _StemCandidate:
    stem: str
    from_url: bool


@dataclass(frozen=True, slots=True)
class _ScoredStem:
    stem: str
    quality: int


def suggest_clean_filename_stem(
    current_stem: str,
    *,
    title: str = "",
    source_url: str = "",
    related_article: str = "",
    max_len: int = 80,
) -> str:
    """Suggest a cleaner filename stem while preserving social/collision suffixes."""
    stem = (current_stem or "").strip()

    social_suffix = ""
    if stem.lower().endswith("-social"):
        social_suffix = "-social"
        stem = stem[: -len(social_suffix)]

    collision_suffix = ""
    match = re.search(r"-([a-f0-9]{4})$", stem)
    if match:
        collision_suffix = f"-{match.group(1)}"
        stem = stem[: -len(collision_suffix)]

    candidates: list[_StemCandidate] = []
    related_stem = _extract_related_article_stem(related_article)
    if social_suffix and related_stem:
        candidates.append(_StemCandidate(related_stem, from_url=False))

    clean_title = clean_ws(title)
    if clean_title:
        clean_title = re.sub(r"(?i)^social draft for\s+", "", clean_title).strip()
        clean_title = _strip_source_host_prefix(clean_title, source_url)
        clean_title = _strip_host_brand_suffix(clean_title, source_url)
        clean_title = _expand_common_stem_terms(clean_title)
        clean_title = _remove_domain_like_lead(clean_title)
        candidates.append(_StemCandidate(clean_title, from_url=False))

    if looks_like_urlish_stem(stem):
        candidates.append(_StemCandidate(_derive_stem_from_urlish_stem(stem), from_url=True))

    if source_url:
        candidates.append(_StemCandidate(_derive_stem_from_url(source_url), from_url=True))

    normalized_stem = _normalize_current_stem(stem)
    if normalized_stem:
        normalized_stem = _strip_host_brand_suffix(normalized_stem, source_url)
        normalized_stem = _remove_domain_like_lead(normalized_stem)
        candidates.append(_StemCandidate(normalized_stem, from_url=False))

    viable_candidates: list[_ScoredStem] = []
    fallback_candidates: list[_ScoredStem] = []
    for candidate in candidates:
        if not candidate.stem:
            continue
        scored = _ScoredStem(
            candidate.stem, _candidate_quality(candidate.stem, source_url=source_url, from_url=candidate.from_url)
        )
        if looks_like_url(candidate.stem) or looks_like_urlish_stem(candidate.stem):
            fallback_candidates.append(scored)
            continue
        viable_candidates.append(scored)

    if viable_candidates:
        base = max(viable_candidates, key=lambda item: item.quality).stem
    elif fallback_candidates:
        base = max(fallback_candidates, key=lambda item: item.quality).stem
    else:
        base = "untitled"

    suggested = slugify_note_stem(base, max_len=max_len)
    final_stem = f"{suggested}{collision_suffix}{social_suffix}"
    return sanitize_filename(final_stem, max_len=max_len + len(collision_suffix) + len(social_suffix))


def get_vault_stats(vault_path: Path) -> dict[str, int]:
    """Return basic counts of markdown files across PARA top-level folders."""
    vault_dir = vault_path / "Vault" if (vault_path / "Vault").exists() else vault_path
    stats: dict[str, int] = {}
    if vault_dir.exists():
        for folder in sorted(vault_dir.iterdir()):
            if folder.is_dir() and not folder.name.startswith("."):
                count = len(list(folder.rglob("*.md")))
                if count:
                    stats[folder.name] = count
    stats["Total"] = sum(stats.values())
    return stats
