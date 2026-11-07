"""Tests for license management and SPDX compliance engine."""

import datetime
from pathlib import Path

from rig.repo_governance.license_manager import (
    align_license,
    audit_spdx_headers,
    detect_license_type,
    generate_license_content,
    inspect_license,
)


def test_detect_license_type():
    mit_sample = "MIT License\nPermission is hereby granted, free of charge..."
    apache_sample = "Apache License\nVersion 2.0, January 2004\nhttp://www.apache.org/licenses/"
    bsd_sample = "BSD 3-Clause License\nRedistribution and use in source and binary forms..."
    unlicense_sample = "This is free and unencumbered software released into the public domain."
    unknown_sample = "Custom Proprietary License - All Rights Reserved."

    assert detect_license_type(mit_sample) == "MIT"
    assert detect_license_type(apache_sample) == "Apache-2.0"
    assert detect_license_type(bsd_sample) == "BSD-3-Clause"
    assert detect_license_type(unlicense_sample) == "Unlicense"
    assert detect_license_type(unknown_sample) is None


def test_generate_license_content():
    year = datetime.datetime.now(datetime.UTC).year
    content = generate_license_content("MIT", copyright_holder="Ryan Kelly")
    assert f"Copyright (c) {year} Ryan Kelly" in content
    assert "MIT License" in content

    range_content = generate_license_content("MIT", copyright_holder="Ryan Kelly", start_year=2020)
    assert f"Copyright (c) 2020-{year} Ryan Kelly" in content or f"2020-{year}" in range_content


def test_inspect_and_align_license(tmp_path: Path):
    # 1. Missing license
    res = inspect_license(tmp_path, expected_type="MIT", copyright_holder="Ryan Kelly")
    assert not res.exists
    assert not res.is_aligned
    assert any("No LICENSE file found" in d for d in res.details)

    # 2. Dry-run align
    aligned = align_license(tmp_path, license_type="MIT", copyright_holder="Ryan Kelly", dry_run=True)
    assert aligned.changed
    assert "[Dry Run]" in aligned.message
    assert not (tmp_path / "LICENSE").exists()

    # 3. Actual align
    aligned = align_license(tmp_path, license_type="MIT", copyright_holder="Ryan Kelly", dry_run=False)
    assert aligned.changed
    assert (tmp_path / "LICENSE").exists()

    # 4. Inspect aligned license
    res = inspect_license(tmp_path, expected_type="MIT", copyright_holder="Ryan Kelly")
    assert res.exists
    assert res.is_aligned
    assert res.detected_type == "MIT"
    assert res.copyright_holder_valid
    assert res.copyright_years_valid

    # 5. Idempotent re-align
    aligned = align_license(tmp_path, license_type="MIT", copyright_holder="Ryan Kelly", dry_run=False)
    assert not aligned.changed
    assert "Already up to date" in aligned.message


def test_inspect_wrong_license_type(tmp_path: Path):
    (tmp_path / "LICENSE").write_text(
        "Apache License\nVersion 2.0, January 2004\nCopyright 2026 Ryan Kelly",
        encoding="utf-8",
    )
    res = inspect_license(tmp_path, expected_type="MIT", copyright_holder="Ryan Kelly")
    assert res.exists
    assert not res.is_aligned
    assert res.detected_type == "Apache-2.0"
    assert any("differs from expected 'MIT'" in d for d in res.details)


def test_audit_spdx_headers(tmp_path: Path):
    src_dir = tmp_path / "src"
    src_dir.mkdir()

    # File with SPDX header
    valid_file = src_dir / "index.ts"
    valid_file.write_text("// SPDX-License-Identifier: MIT\nconsole.log('hello');", encoding="utf-8")

    # File without SPDX header
    missing_file = src_dir / "utils.ts"
    missing_file.write_text("console.log('no header');", encoding="utf-8")

    # Ignored directory file
    node_modules = tmp_path / "node_modules" / "pkg"
    node_modules.mkdir(parents=True)
    (node_modules / "pkg.js").write_text("console.log('ignore');", encoding="utf-8")

    scan = audit_spdx_headers(tmp_path, expected_spdx_id="MIT")
    assert scan.total_files == 2  # index.ts and utils.ts, ignoring node_modules
    assert len(scan.missing_spdx) == 1
    assert scan.missing_spdx[0] == Path("src/utils.ts")
