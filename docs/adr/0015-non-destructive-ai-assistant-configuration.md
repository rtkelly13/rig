# ADR 0015: Non-Destructive AI Assistant Baseline Configuration

- **Status:** Accepted
- **Applies to:** `devenv` AI coding assistant setup step

## Context
AI coding assistants (Claude Code, OpenCode, Codex) require safe baseline permissions for read/test execution (`git`, `uv`, `pytest`, `ruff`, `pyright`, `gh`) to prevent repetitive manual confirmation fatigue. Overwriting user configuration files destroys personal preferences, custom MCP servers, and API tokens.

## Decision
Apply non-destructive union merges when configuring AI coding tools:
- `~/.claude/settings.json`: Merges the policy's safe commands into `permissions.allow` and its denied commands into `permissions.deny` without modifying personal preferences or keys. Sets baseline preferences and clears automated attribution trailers and PR footers (`attribution.commit`, `attribution.pr`, `attribution.sessionUrl`).
- `~/.claude/settings.json` `hooks.SessionEnd`: Adds the session-ledger hook (below) unless a `SessionEnd` hook already runs a script named `session-ledger.mjs`; other hooks are kept.
- `~/.config/opencode/opencode.json`: Merges granular `permission.bash` rules while preserving existing keys.
- `~/.gemini/antigravity-cli/settings.json`: Merges missing safe commands into `permissions.allow` and denied commands into `permissions.deny`, using `command(...)` rules only. Antigravity deprecated the `unsandboxed(...)` form and reports it as invalid on load, so existing rules are rewritten to `command(...)` and deduplicated during sync.
- `~/.agent-browser-profiles/`: Provisions isolated `dev` and `personal` browser data directories with shell environment helpers.

Run `uv run workspace policy sync --dry-run` to preview projections. The generated files are
adapters for each harness; `config/cli-allowlists.json` remains the source of truth. `devenv claude`
and `devenv ai` run the same projection for Claude Code and OpenCode rather than carrying a baseline
of their own: an earlier hard-coded devenv list (`Bash(git:*)`, `curl`, `pkill`, `python:*`) granted
far more than the policy allows (`git reset --hard`, `git clean`, arbitrary scripts).

Nothing is installed into a harness's runtime. `workspace policy exec` and the
`estate-guard` plugin were both removed: each re-implemented a decision the harness
already makes, and the plugin broke silently when OpenCode moved from its v1 to its v2
plugin API. Harnesses enforce the projected rules themselves — see
[ADR 0021](0021-projections-not-runtime-interposition.md).

### Session-end ledger

`uv run devenv claude` (also part of `devenv ai` and `devenv setup`) copies
`steps/ai_tools/claude_hooks/session-ledger.mjs` to `~/.claude/hooks/session-ledger.mjs` and wires it as a
`SessionEnd` hook (`node "<path>"`, 5s timeout). The workspace repo's devenv installs the same script to the same
path and detects an existing install by the same rule, so either repo can install it and neither adds a second
entry. A script that differs from the bundled copy (ignoring line endings) is replaced. `devenv doctor` reports
the hook as installed, not wired, script missing, or script out of date. Settings are only written when
`settings.json` parses as a JSON object, and are replaced atomically.

The hook writes one file per session to `~/.claude/session-ends/<session_id>.json` (or
`$CLAUDE_SESSION_LEDGER_DIR/<session_id>.json`). This is the contract
`desktop_manager.claude_sessions.discovery` reads:

```json
{
  "session_id": "<id matching [A-Za-z0-9_-]{1,128}>",
  "reason": "clear | prompt_input_exit | logout | other | ... (null when absent)",
  "cwd": "<string or null>",
  "transcript_path": "<string or null>",
  "ended_at": "<ISO 8601 UTC timestamp>"
}
```

`reason: "clear"` means the user ran `/clear` and the session is finished. A crash fires no hook, so a missing
record means nothing. A record whose `ended_at` is older than the transcript's last activity is stale and ignored.

## Consequences
- Reduces confirmation friction during AI pair programming sessions.
- Guaranteed safety against configuration data loss.
- Keeps unknown and external commands at the harness approval boundary rather than broadening the allowlist.

## References
- [`src/rig/devenv/steps/ai_tools/claude_code.py`](../../src/rig/devenv/steps/ai_tools/claude_code.py)
- [`src/rig/devenv/steps/ai_tools/session_ledger.py`](../../src/rig/devenv/steps/ai_tools/session_ledger.py)
