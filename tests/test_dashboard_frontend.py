"""Tests for Asset Studio template integrity, HTML structure, and JS controller."""

from __future__ import annotations

from pathlib import Path

import pytest

pytestmark = pytest.mark.ci


def test_index_html_structure() -> None:
    """Verify HTML template structure and brutalist design system token usage."""
    template_path = Path(__file__).parent.parent / "src" / "rig" / "web_host" / "templates" / "index.html"
    assert template_path.exists(), "index.html must exist in templates directory"

    content = template_path.read_text(encoding="utf-8")

    # Verify midnight theme data attribute
    assert 'data-theme="midnight"' in content

    # Verify bracketed headers and indicators
    assert 'id="view-workbench"' in content
    assert "[ REFERENCE_SEQUENCE ]" in content
    assert "[ PRODUCTION_CUTS ]" in content
    assert "[ VARIATIONS_&_SNAPSHOTS ]" in content

    # Verify absence of agent feedback console (removed for pure visual comparison focus)
    assert "AGENT_FEEDBACK_CONSOLE" not in content
    assert 'id="feedback-notes"' not in content
    assert 'id="btn-copy-prompt"' not in content
    assert 'id="btn-save-feedback"' not in content

    # Verify linkage to vendored design system and custom studio styles
    assert 'href="/static/app_assets/app.css"' in content
    assert 'href="/static/studio.css"' in content
    assert 'src="/static/app_assets/app.js"' in content
    assert 'id="legacy-workspace" hidden' in content

    # Verify comparison modes & controls
    assert 'id="mode-side-by-side"' in content
    assert 'id="mode-split-slider"' in content
    assert 'id="mode-diff-heatmap"' in content
    assert 'id="btn-quick-snapshot"' in content
    assert 'id="studio-status"' in content


def test_studio_css_tokens() -> None:
    """Verify studio.css strictly adheres to @rtkelly13/design-system brutalist rules."""
    css_path = Path(__file__).parent.parent / "src" / "rig" / "web_host" / "templates" / "studio.css"
    assert css_path.exists()
    content = css_path.read_text(encoding="utf-8")

    # Enforce 0px border-radius
    assert "border-radius: 0px" in content
    assert "border-radius: 4px" not in content
    assert "border-radius: 8px" not in content

    # Enforce role-based CSS variables
    assert "var(--ds-surface-base" in content
    assert "var(--ds-accent-primary" in content
    assert "var(--ds-font-mono" in content


def test_studio_js_controller() -> None:
    """Verify studio.js defines expected event listeners and API handlers."""
    js_path = Path(__file__).parent.parent / "src" / "rig" / "web_host" / "templates" / "studio.js"
    assert js_path.exists()
    content = js_path.read_text(encoding="utf-8")

    # Check for core API endpoints
    assert "/api/projects" in content
    assert "/api/diff" in content
    assert "/api/snapshot" in content

    # Check for view modes and drag handling
    assert "side-by-side" in content
    assert "split-slider" in content
    assert "diff-heatmap" in content
    assert "updateSliderPosition" in content
    assert "applyZoom" in content

    # Check that feedback console logic is removed
    assert "buildPrompt" not in content
    assert "dom.btnCopyPrompt" not in content
    assert "dom.btnSaveFeedback" not in content
