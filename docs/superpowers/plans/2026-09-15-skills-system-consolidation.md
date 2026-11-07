# Skills System Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `shared_utilities.skills` the single implementation for skill discovery and global synchronization, then expose a curated estate-wide catalog through an explicit allowlist.

**Architecture:** Keep vendored packages as source material, but make the new skills engine authoritative for parsing, discovery, filtering, and symlink mirroring. Preserve the legacy `devenv` command/API surface through thin adapters. Use a checked-in default-deny allowlist for globally active skills.

**Tech Stack:** Python 3.13, Typer, Pydantic, PyYAML, pytest.

**Spec:** This plan implements the user request to consolidate the duplicate skill systems and evaluate overlapping skill families.

## Global Constraints

- Use `uv` for Python commands.
- Preserve existing `workspace skills` behavior and legacy `devenv skills` compatibility.
- Do not delete vendored skill source packages as part of catalog reduction.
- Keep global synchronization idempotent and prune only entries outside the active catalog.

---

### Task 1: Make the canonical engine parse skill metadata

**Files:**
- Modify: `src/shared_utilities/skills/engine.py`
- Modify: `tests/test_skills_symlinks.py`

- [ ] Add tests for folded/block YAML descriptions and empty descriptions.
- [ ] Replace line-based metadata parsing with `yaml.safe_load` and preserve fallback behavior for malformed files.
- [ ] Run the focused skills tests.

### Task 2: Replace the legacy implementation with adapters

**Files:**
- Modify: `src/shared_utilities/devenv/steps/ai_tools/skills.py`
- Modify: `src/shared_utilities/devenv/cli.py`
- Modify: `tests/devenv/test_skills.py`

- [ ] Keep legacy public names but delegate discovery and installation to `shared_utilities.skills`.
- [ ] Keep plugin registration/sync as compatibility operations, then invoke canonical synchronization.
- [ ] Verify both CLI entrypoint families reference one discovery implementation.

### Task 3: Add the curated estate catalog

**Files:**
- Create: `config/skills-allowlist.json`
- Modify: `tests/test_skills_symlinks.py`
- Modify: `README.md`

- [ ] Add a default-deny allowlist containing the reviewed core and specialized skills.
- [ ] Add tests proving unlisted vendored skills are not discovered or synchronized.
- [ ] Document that vendoring and global activation are separate decisions.

### Task 4: Verify and report overlap

**Files:**
- Modify: `config/upstream-skills.json` only if extraction can be reduced safely.
- Create: `docs/agents/skills-catalog.md`

- [ ] Record family decisions and retained alternatives.
- [ ] Run lint, type checking, focused tests, and the full CI-safe test suite where the environment permits.
- [ ] Check the resulting catalog size and metadata quality.
