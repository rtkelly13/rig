# Windows Workspace (desktop-manager)

`desktop-manager workspace` captures and restores a Windows virtual-desktop setup:
it recreates the planned desktops, reuses or launches each planned window, and
places it on the right monitor.

## Typical flow

```bash
uv run desktop-manager workspace wire-machine --workspace C:/code/workspace --personal-workspace C:/code/personal/rig
uv run desktop-manager workspace validate-plan
uv run desktop-manager workspace restore
uv run desktop-manager jobs install
```

`uv run desktop-manager workspace --help` lists every command and flag.

## Guides

| Guide | When to use |
|:------|:------------|
| [restore.md](restore.md) | Restore modes, restoring one desktop or app, inspecting live state, desktop names |
| [plan-files.md](plan-files.md) | Writing or exporting a plan: entries, placement, placeholders, bundles, validation |
| [feature-flags.md](feature-flags.md) | Pausing a group of entries or a whole desktop without deleting it |
| [jobs.md](jobs.md) | The logon, every-15-minute and daily jobs, and the scheduled tasks that run them |
| [startup-and-display-guard.md](startup-and-display-guard.md) | Restore at sign-in, and keeping windows in place across monitor drop-outs and sleep |
| [design.md](design.md) | Why the plan is curated rather than inferred, and the current machine layout |
| [runtime.md](runtime.md) | The pinned copy the scheduled tasks run from: install, status, rollback |
| [work-items.md](work-items.md) | `work list`: branches, worktrees, PRs and Claude sessions joined per repo and branch, and its JSON schema |
| [chrome-tabs-uia.md](chrome-tabs-uia.md) | `chrome-tabs list/prune`: reading and closing Chrome tabs through UI Automation |

## Where things live

- Default plan: `%APPDATA%\workspace-manager\windows-workspace.json`. The machine's
  curated plan is `desktop-manager/workspace-plan.bundle.json` plus `desktop-manager/bundles/`.
- Logs: a `logs/` directory beside the plan file, for restores and the display guard.
- Traces: every run writes OTLP-JSON spans for restore phases, desktops, entries,
  virtual-desktop calls and helper subprocesses. See [observability](../observability.md).
- Code: `src/desktop_manager/`.

## Testing

Tests must never touch the live virtual desktops. The integration fixtures fake
desktop renames, and `tests/conftest.py` fails any test that reaches a real pyvda
rename or remove.
