# Agent Permissions Stack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Project one estate command policy into native agent permission files, provide a safe enforced execution boundary, and make hidden skill activation reversible.

**Architecture:** `shared_utilities.policy` remains the source of truth for command risk and owns native projection adapters. Each adapter preserves unmanaged user configuration and replaces only a marked block or missing keys. `workspace policy exec` executes argv directly after boundary validation; shell composition, unknown commands, external mutations without approval, and denied commands never cross the subprocess boundary. `shared_utilities.skills` owns a small user-local activation ledger so targeted and full-catalog overlays can be removed without touching the checked-in catalog.

**Tech Stack:** Python 3.13, Typer, Pydantic, JSON/TOML/Starlark-compatible text, pytest.

**Spec:** `docs/adr/0015-non-destructive-ai-assistant-configuration.md` and the native harness documentation linked from the PR description.

## Global Constraints

- Do not overwrite unmanaged agent configuration, API tokens, plugins, or user permission rules.
- Use `uv run` for Python commands.
- Keep the checked-in command policy as the only source of truth for generated native rules.
- Treat `safe` as read-only validation; `local` and `external` require explicit approval at the execution boundary.
- Treat `denied` as an unconditional rejection in the execution wrapper.
- Do not claim persistent AGY command allowlist support without a native AGY configuration surface.
- All state-changing operations must be idempotent and safe to repeat.

---

### Task 1: Native permission projections

**Files:**
- Create: `src/shared_utilities/policy/projections.py`
- Modify: `src/shared_utilities/policy/models.py`
- Modify: `src/shared_utilities/policy/engine.py`
- Modify: `src/shared_utilities/policy/cli.py`
- Modify: `src/shared_utilities/policy/__init__.py`
- Modify: `tests/test_policy.py`
- Modify: `docs/adr/0015-non-destructive-ai-assistant-configuration.md`

**Interfaces:**
- `project_native_permissions(systems, home, dry_run) -> list[ProjectionResult]`
- `get_native_projection(system, home) -> NativeProjection`
- `NativeProjection.render(policy) -> str | dict[str, object]`
- `NativeProjection.apply(policy, dry_run) -> ProjectionResult`

- [ ] Write tests for Claude, Codex, and OpenCode projection rendering; repeated application must produce no duplicate managed rules.
- [ ] Write a test proving existing user keys and unmanaged rules remain unchanged.
- [ ] Write a test proving AGY reports unsupported without writing a file.
- [ ] Implement command-prefix extraction at the policy boundary and native adapters for each supported harness.
- [ ] Add `workspace policy sync [--system ...] [--dry-run]` and render a concise result table.
- [ ] Document generated ownership markers and the AGY limitation.

### Task 2: Enforced argv command boundary

**Files:**
- Create: `src/shared_utilities/policy/runner.py`
- Modify: `src/shared_utilities/policy/engine.py`
- Modify: `src/shared_utilities/policy/cli.py`
- Modify: `tests/test_policy.py`
- Modify: `docs/adr/0015-non-destructive-ai-assistant-configuration.md`

**Interfaces:**
- `parse_direct_command(args) -> list[str]`
- `run_policy_command(args, policy, approve, cwd) -> int`

- [ ] Write failing tests for empty argv, shell operators, command substitution, unknown commands, denied commands, and approval requirements.
- [ ] Write a test proving approved argv is passed to `subprocess.run` without `shell=True`.
- [ ] Implement `workspace policy exec -- [argv...]` with direct subprocess execution and exit-code propagation.
- [ ] Add `--approve` and `--cwd`; require `--approve` for local/external risks and reject denied risk regardless of flags.
- [ ] Update `check` so composed shell input is denied rather than matched as a single string.
- [ ] Document that this is the enforcement boundary agents should call, not a replacement for native harness policy.

### Task 3: Reversible skill activation overlays

**Files:**
- Modify: `src/shared_utilities/skills/engine.py`
- Modify: `src/shared_utilities/skills/cli.py`
- Modify: `src/shared_utilities/skills/__init__.py`
- Modify: `tests/test_skills_symlinks.py`
- Modify: `docs/agents/skills-catalog.md`

**Interfaces:**
- `activate_skills(names, systems, home, all_skills, dry_run) -> dict[CliSystem, int]`
- `deactivate_skills(names, systems, home, all_skills, dry_run) -> dict[CliSystem, int]`
- `load_skill_activation_state(home) -> list[str]`

- [ ] Write tests for targeted activation, full activation, state persistence, idempotent repeat activation, and selective deactivation.
- [ ] Implement a user-local activation ledger and remove only links still pointing at the recorded managed skill.
- [ ] Route `skills sync --only` and `skills sync --all` through the ledger so the documented temporary behavior is true.
- [ ] Add `skills activate` and `skills deactivate` commands with explicit `--only`/`--all` selection.
- [ ] Document the activation/deactivation workflow and state location.

### Verification gate

- [ ] Run focused policy and skill tests.
- [ ] Run `uv run ruff check src tests` and `uv run pyright`.
- [ ] Run the complete `uv run pytest` suite and record the result before opening PRs.

