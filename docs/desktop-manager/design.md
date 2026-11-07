# Windows Workspace Design

## Goal

Restore the machine's virtual desktops and key apps after sign-in, with an opinionated
plan tuned for this machine rather than a generic capture of whatever is open.

## Current layout

The curated layout is the source of truth in `desktop-manager/workspace-plan.bundle.json`
and `desktop-manager/bundles/`: desktop names, entries, placements and launch delays.
Run `uv run desktop-manager workspace features` or `status` to see it resolved.

## Design principles

- Prefer a curated machine plan over live desktop inference.
- Treat this as workspace management, not tool-specific automation.
- Keep the plan inspectable and editable as JSON.
- Make terminal behaviour explicit and deterministic.
- Use practical per-app delays rather than perfect window choreography.
- Optimise for restart reliability first, refinement second.

## Why a curated plan

The hard problem is not launching applications. It is deciding what belongs on each
desktop and recreating that layout predictably after a restart.

An earlier capture-based version showed the weakness of generic automation on Windows
virtual desktops: visible windows can be misattributed across desktops. So restore
never infers the layout. It replays a curated plan, and `export-plan` only drafts a
starting point for one. A curated plan is easier to reason about, edit and test, and
it behaves the same on every restart.

## Restore model

- Restore diffs the plan against live windows and reuses matches before launching
  anything, so re-running it on a working layout is cheap and safe.
- Placement uses each monitor's work area, by screen number, after switching to the
  target desktop.
- Desktop names are re-applied on every run, because Windows drops them.

## Display guard

Restore runs once, at sign-in. Monitors that drop out during sleep scramble windows
long after that, so a separate long-running guard owns recovery:

- It snapshots placements per monitor topology. After a change it restores the
  snapshot for the topology that came back, not the latest snapshot, which would
  already be scrambled.
- It waits for the topology to settle before acting, then checks again, because
  Windows keeps moving windows for a few seconds after a monitor returns.
- It only moves windows. Relaunching closed apps stays restore's job, and the
  restore lock stops the two from fighting.

## Known risks

- Teams may not create two distinct windows, depending on its process model and
  prior session state.
- Some apps restore their own prior state instead of launching clean.
- The Obsidian vault repository has Windows-incompatible filenames in its Git
  history, so the local clone is usable but not a clean checkout.

## Future direction

- Named machine profiles and configurable workspace roots.
- Richer app adapters for Chrome, Slack, Obsidian and Teams, such as restoring
  Chrome tabs.
- A more generic capture, edit and restore workflow for machines where a curated
  plan is not worth maintaining.
