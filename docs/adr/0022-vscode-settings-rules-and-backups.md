# ADR 0022: VS Code settings rules and local backups

- **Status:** Accepted
- **Applies to:** `devenv vscode` and the "VS Code Settings" step of `devenv setup`

## Context

VS Code Settings Sync carries settings between machines, but nothing records which settings rig's own
tooling depends on, and Sync keeps no history rig can restore from.

One setting matters to rig directly. Worktrees live inside the repos they belong to (`.worktrees/`,
`temp/rig/<name>/`), so a VS Code window on a repo also watches every worktree in it. Creating a worktree,
resetting it onto new history or running `uv sync` / `pnpm install` in it writes tens of thousands of files
(about 1,100 tracked, 15,000 in `.venv`, 21,000 in `node_modules`). Extensions react to the change events by
re-scanning the workspace, and each scan starts `rg` processes. On 2026-11-06 a window ran about 1,100 `rg`
processes at once (16 GB) within 30 seconds of a worktree being created; on 2026-11-09 the extension host
was starved for an hour while worktrees were rebased, until VS Code was closed.

## Decision

`src/rig/devenv/config/vscode/settings.json` lists the settings every machine must have. Today that is
`files.exclude` and `files.watcherExclude` for `**/.worktrees` and `**/temp`. `files.exclude` also hides
them from search and from extensions' `workspace.findFiles`.

`uv run devenv vscode` merges those rules into the user `settings.json` without overwriting:

- A missing key, or a missing entry in an object such as `files.exclude`, is inserted as text after the
  object's `{`. The rest of the file, including comments and trailing commas, is untouched. `rig.jsonc`
  parses the file and finds the insertion points.
- A key the user has set to a different value is reported as a conflict and left alone (exit 1).
- `--dry-run` prints the plan (exit 1 when anything would change). A second run finds nothing to do.

Before every write, and with `--backup-only`, the `User` folder's `settings.json`, `keybindings.json`,
`mcp.json`, `tasks.json`, `snippets/` and `prompts/`, plus `code --list-extensions --show-versions`, are
copied to `<app data>/backups/vscode/<UTC timestamp>/`. The newest ten are kept. Backups stay on the machine:
`mcp.json` can hold tokens.

## Consequences

- Settings Sync stays the way settings move between machines; rig owns only the rules it depends on.
- A rule wins only where the user has not decided otherwise, matching
  [ADR 0015](0015-non-destructive-ai-assistant-configuration.md).
- `**/temp` hides a folder named `temp` in any repo opened in VS Code, not only rig's scratch folders.
- Restoring is a copy back from a snapshot folder; there is no restore command.

## References

- [`src/rig/devenv/steps/vscode.py`](../../src/rig/devenv/steps/vscode.py)
- [`src/rig/jsonc.py`](../../src/rig/jsonc.py)
