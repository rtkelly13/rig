"""License management and SPDX compliance engine for open source repositories.

Audits and enforces canonical open source licenses (MIT, Apache-2.0, BSD-3-Clause, Unlicense)
and validates copyright holder and year ranges.
"""

from __future__ import annotations

import datetime
import re
from dataclasses import dataclass
from enum import StrEnum
from pathlib import Path

from pydantic import BaseModel, Field

from rig.repo_governance.policy_config import load_policy_config
from rig.repo_governance.policy_key import policy_key

# Standard SPDX License Templates
MIT_TEMPLATE = """MIT License

Copyright (c) {year} {holder}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"""

APACHE_2_0_TEMPLATE = """Apache License
Version 2.0, January 2004
http://www.apache.org/licenses/

Copyright {year} {holder}

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
"""

BSD_3_CLAUSE_TEMPLATE = """BSD 3-Clause License

Copyright (c) {year}, {holder}
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
"""

UNLICENSE_TEMPLATE = """This is free and unencumbered software released into the public domain.

Anyone is free to copy, modify, publish, use, compile, sell, or
distribute this software, either in source code form or as a compiled
binary, for any purpose, commercial or non-commercial, and by any
means.

In jurisdictions that recognize copyright laws, the author or authors
of this software dedicate any and all copyright interest in the
software to the public domain. We make this dedication for the benefit
of the public at large and to the detriment of our heirs and
successors. We intend this dedication to be an overt act of
relinquishment in perpetuity of all present and future rights to this
software under copyright law.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS BE LIABLE FOR ANY CLAIM, DAMAGES OR
OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
OTHER DEALINGS IN THE SOFTWARE.

For more information, please refer to <http://unlicense.org/>
"""

LICENSE_TEMPLATES: dict[str, str] = {
    "MIT": MIT_TEMPLATE,
    "Apache-2.0": APACHE_2_0_TEMPLATE,
    "BSD-3-Clause": BSD_3_CLAUSE_TEMPLATE,
    "Unlicense": UNLICENSE_TEMPLATE,
}


class LicenseType(StrEnum):
    MIT = "MIT"
    APACHE_2_0 = "Apache-2.0"
    BSD_3_CLAUSE = "BSD-3-Clause"
    UNLICENSE = "Unlicense"


class LicenseAuditResult(BaseModel):
    repo_name: str
    exists: bool
    license_file: str | None = None
    detected_type: str | None = None
    expected_type: str
    copyright_holder_valid: bool = False
    copyright_years_valid: bool = False
    is_aligned: bool = False
    details: list[str] = Field(default_factory=list)


@dataclass(frozen=True, slots=True)
class SPDXScanResult:
    total_files: int
    missing_spdx: list[Path]


@dataclass(frozen=True, slots=True)
class LicenseAlignment:
    """Whether :func:`align_license` changed (or, on a dry run, would change) the LICENSE, and what it did."""

    changed: bool
    message: str


def find_license_file(repo_path: Path) -> Path | None:
    """Finds existing license file in repository root."""
    for candidate in ("LICENSE", "LICENSE.md", "LICENSE.txt", "license", "license.md"):
        path = repo_path / candidate
        if path.is_file():
            return path
    return None


def detect_license_type(content: str) -> str | None:
    """Infers the license type based on hallmark text signatures."""
    text_lower = content.lower()
    if "permission is hereby granted, free of charge" in text_lower and "mit license" in text_lower:
        return "MIT"
    if "apache license" in text_lower and "version 2.0" in text_lower:
        return "Apache-2.0"
    if "bsd 3-clause license" in text_lower or (
        "redistribution and use in source and binary forms" in text_lower and "neither the name" in text_lower
    ):
        return "BSD-3-Clause"
    if "free and unencumbered software released into the public domain" in text_lower:
        return "Unlicense"
    return None


def inspect_license(
    repo_path: Path,
    expected_type: str | None = None,
    copyright_holder: str | None = None,
) -> LicenseAuditResult:
    """Audits repository root for canonical license presence, type, copyright holder, and year."""
    cfg = load_policy_config()
    key = policy_key(repo_path)
    target_type = expected_type or cfg.license_type(key)
    target_holder = copyright_holder or cfg.copyright_holder(key)

    repo_name = repo_path.name
    license_file = find_license_file(repo_path)

    if not license_file:
        return LicenseAuditResult(
            repo_name=repo_name,
            exists=False,
            expected_type=target_type,
            is_aligned=False,
            details=["No LICENSE file found in repository root."],
        )

    content = license_file.read_text(encoding="utf-8", errors="replace")
    detected = detect_license_type(content)

    details: list[str] = []
    if detected != target_type:
        details.append(f"Detected license '{detected}' differs from expected '{target_type}'.")

    # Check copyright holder
    holder_valid = target_holder.lower() in content.lower()
    if not holder_valid and target_type != "Unlicense":
        details.append(f"Copyright holder '{target_holder}' not found in license.")

    # Check copyright year
    current_year = str(datetime.datetime.now(datetime.UTC).year)
    year_valid = current_year in content or "present" in content.lower()
    if not year_valid and target_type != "Unlicense":
        details.append(f"Current year ({current_year}) or 'present' not found in copyright line.")

    is_aligned = (
        (detected == target_type)
        and (holder_valid or target_type == "Unlicense")
        and (year_valid or target_type == "Unlicense")
    )

    return LicenseAuditResult(
        repo_name=repo_name,
        exists=True,
        license_file=license_file.name,
        detected_type=detected,
        expected_type=target_type,
        copyright_holder_valid=holder_valid,
        copyright_years_valid=year_valid,
        is_aligned=is_aligned,
        details=details,
    )


def generate_license_content(
    license_type: str = "MIT",
    copyright_holder: str = "Ryan Kelly",
    start_year: int | None = None,
) -> str:
    """Renders canonical license text with the specified holder and year range."""
    template = LICENSE_TEMPLATES.get(license_type, MIT_TEMPLATE)
    current_year = datetime.datetime.now(datetime.UTC).year
    year_str = f"{start_year}-{current_year}" if start_year and start_year < current_year else str(current_year)

    return template.format(year=year_str, holder=copyright_holder)


_COPYRIGHT_LINE = re.compile(r"^\s*Copyright\b.*$", re.MULTILINE | re.IGNORECASE)
_YEAR_START = re.compile(r"\b((?:19|20)\d{2})\b")
_YEAR_RANGE = re.compile(r"\b(?:19|20)\d{2}(?:\s*-\s*(?:19|20)\d{2})?\b")


def _preserve_existing_notices(generated: str, existing: str, holder: str) -> str:
    """Keep existing copyright lines (other holders verbatim; ours with its original start year).

    MIT/BSD/Apache require existing notices be retained, so a policy holder is added alongside
    them rather than replacing them.
    """
    existing_lines = [m.group(0) for m in _COPYRIGHT_LINE.finditer(existing)]
    new_match = _COPYRIGHT_LINE.search(generated)
    if not existing_lines or not new_match:
        return generated
    kept: list[str] = []
    has_holder = False
    for line in existing_lines:
        if holder.lower() in line.lower():
            has_holder = True
            now = datetime.datetime.now(datetime.UTC).year
            year = _YEAR_START.search(line)
            if year:
                start = int(year.group(1))
                rng = f"{start}-{now}" if start < now else str(now)
                line = _YEAR_RANGE.sub(rng, line, count=1)
            kept.append(line.rstrip())
        else:
            kept.append(line.rstrip())
    if not has_holder:
        kept.append(new_match.group(0))
    return generated[: new_match.start()] + "\n".join(kept) + generated[new_match.end() :]


def align_license(
    repo_path: Path,
    license_type: str | None = None,
    copyright_holder: str | None = None,
    start_year: int | None = None,
    dry_run: bool = False,
) -> LicenseAlignment:
    """Ensures canonical LICENSE file exists and is formatted correctly."""
    cfg = load_policy_config()
    key = policy_key(repo_path)
    target_type = license_type or cfg.license_type(key)
    target_holder = copyright_holder or cfg.copyright_holder(key)

    target_file = repo_path / "LICENSE"
    content = generate_license_content(
        license_type=target_type,
        copyright_holder=target_holder,
        start_year=start_year,
    )

    if target_file.exists():
        current_content = target_file.read_text(encoding="utf-8", errors="replace")
        content = _preserve_existing_notices(content, current_content, target_holder)
        if current_content.strip() == content.strip():
            return LicenseAlignment(changed=False, message="Already up to date.")

    if not dry_run:
        target_file.write_text(content, encoding="utf-8")
        return LicenseAlignment(changed=True, message=f"Wrote canonical {target_type} LICENSE to {target_file.name}.")
    return LicenseAlignment(
        changed=True, message=f"[Dry Run] Would write canonical {target_type} LICENSE to {target_file.name}."
    )


def audit_spdx_headers(
    repo_path: Path,
    expected_spdx_id: str = "MIT",
    source_extensions: tuple[str, ...] = (
        ".py",
        ".ts",
        ".tsx",
        ".js",
        ".mjs",
        ".cs",
        ".fs",
        ".fsi",
    ),
    ignored_dirs: tuple[str, ...] = (
        ".git",
        ".venv",
        "venv",
        "node_modules",
        "temp",
        "dist",
        "build",
        "out",
        "bin",
        "obj",
        ".next",
    ),
) -> SPDXScanResult:
    """Scans repository source code files for SPDX-License-Identifier preambles."""
    missing: list[Path] = []
    total = 0
    spdx_marker = f"SPDX-License-Identifier: {expected_spdx_id}".lower()

    for ext in source_extensions:
        for file_path in repo_path.rglob(f"*{ext}"):
            # Check ignored components
            if any(ignored in file_path.parts for ignored in ignored_dirs):
                continue
            total += 1
            try:
                # Only check first 15 lines of the file
                with file_path.open("r", encoding="utf-8", errors="replace") as f:
                    header = "".join([f.readline() for _ in range(15)]).lower()
                    if spdx_marker not in header:
                        missing.append(file_path.relative_to(repo_path))
            except OSError:
                continue

    return SPDXScanResult(total_files=total, missing_spdx=missing)
