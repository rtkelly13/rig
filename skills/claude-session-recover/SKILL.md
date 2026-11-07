---
name: claude-session-recover
description: Triage Claude Code sessions that crashed or were closed (finished, waiting on the user, or hung mid-task) and resume the ones that need it inside the Windows Terminal windows already open, driven by UI Automation, or back into the Orca tabs they lived in. Use when the user says Claude sessions crashed, died, or disappeared after a reboot or OOM, says Orca tabs or sessions are missing after a restart, or asks which sessions to reopen, or to resume yesterday's sessions on Windows.
platforms: [Windows]
---

# Claude Session Recover

After a crash or restart, the job is to work out which dead sessions still have work in
them and resume those inside the terminals the user already has open, grouped the way
the user works.

The tool is `desktop-manager claude-sessions` (in the shared-utilities repo). Every
command except `apply` and `recover` is read-only.

**Sessions that lived in Orca tabs** go back into Orca, not Windows Terminal: follow
[Orca tabs lost at restart](#orca-tabs-lost-at-restart) instead of steps 3 to 5.

## 1. Gather the facts

```
desktop-manager claude-sessions list --since 72h --json [--reply-chars 1500]
desktop-manager claude-sessions terminals --json
```

`--since` counts back from each session's *last* message, not its start. A session
left idle since yesterday morning falls outside a 24h window, so use 72h (longer after
a weekend). A crash or reboot often still writes the clean-exit marker, so a whole
batch can show `exited`.

### Sessions the user cleared

When the user runs `/clear`, Claude Code ends that session and carries on under a new
id. A SessionEnd hook records the ending in the **session-end ledger**: one
`<session id>.json` per ended session in `~/.claude/session-ends` (or
`$CLAUDE_SESSION_LEDGER_DIR`), holding `reason` (`clear` for `/clear`) and `ended_at`.
The record is written against the **old** id. The new session starts with no typed
prompts, so it is skipped too until something is typed in it.

`list` leaves cleared sessions out: the user said they were done with them. They
appear under `skipped` with reason `cleared with /clear`, and the human-readable `list`
counts them. If the session's transcript has activity after `ended_at`, it was resumed
and used again, and the ledger record no longer applies. `--include-cleared` offers
them anyway (on `list`, `plan` and `recover`).

### What `list --json` returns

The CLI only gathers facts; the judging is yours.

- `candidates`: sessions not running now, newest first. For each one:
  - `ended`: `crashed` (no clean-exit marker), `exited` (usually a closed tab or a
    restart; it says nothing about whether the work was done), or `cleared` (only with
    `--include-cleared`). `end_reason` is the ledger's raw reason, or null.
  - `turn_state`: how the last turn ended. This is where triage starts.
    - `mid-tool`: a tool call or tool result is last, with no reply after it. The
      session died while working.
    - `unanswered`: the user's prompt (or a compaction summary) is last. The session
      died before answering.
    - `answered`: a reply to the user is last. The turn finished.
  - `last_prompt`, `last_reply_tail` (the end of the last reply) and
    `last_reply_truncated`.
  - `start_dir`: where the session started, and where it resumes.
  - `last_dir`: where it was last working, with `exists`, `git_root`, `branch` and
    `is_worktree`. `moved` is true when it differs from `start_dir`.
- `running`: session ids that are live (process alive, same start time). Never
  offered, never resumed.
- `skipped`: sessions that can't or shouldn't be resumed, each with a `reason`.

`terminals --json` returns the virtual `desktops` (number, name, current) and each
Windows Terminal window's `hwnd`, `desktop`, `placement` (screen slot, e.g.
`screen 1: halves#2/vertical`, or null if unplaced) and `tabs` (`idle` marks a bare
shell prompt).

For a human-readable view, drop `--json`. `show <id> ...` prints one session's facts
with its last exchange.

## 2. Triage: read how each session stopped

The ledger already removed what the user cleared. For the rest, `turn_state` is
mechanical. Before recommending anything, read `last_prompt` and `last_reply_tail`, and
sort the sessions into three groups:

| Group | Signals | Recommendation |
|---|---|---|
| **Hung partway through** | `mid-tool` or `unanswered`. The reply announces work it never reported back ("Running the full suite…", "Starting with two things in parallel…"). | Resume. |
| **Finished, but the user has something to act on** | `answered`, and the reply ends with a question, a decision, an approval, or something the user must send or merge ("I can move those…", "waiting for your call", "delete the draft before sending"). | Resume. Name the action. |
| **Finished, nothing pending** | `answered`, and the reply is a final summary, a handover with a paste-ready prompt, or an explicit wrap-up ("Nothing is running, so you can restart now", "everything is committed and pushed"). | Offer to leave closed. Point to where the work is picked up (issue, PR, handover file). |

Judge from the reply text, not from `ended`. A session the user closed deliberately
can still be mid-task. A crashed one can have finished its turn cleanly. If the tail
is truncated and ambiguous, raise `--reply-chars`.

Present the result as one short table per group. Give each session's id prefix, title
and a one-line reason quoting or paraphrasing how it stopped. For a session that
`moved`, also give its last repo and branch. Then confirm which to reopen. If the user
asks to reopen everything they didn't clear, that is every candidate.

## 3. Group the sessions and fit the desktop layout

You decide the grouping; the tool only finds tabs for it.

The user works across four virtual desktops, defined in shared-utilities by
`desktop-manager/workspace-plan.bundle.json` and `desktop-manager/bundles/*.json`. In
position order they are Review, Work, Admin, Personal. Desktop *names* in
`terminals --json` can be stale after a failed restore (for example "Admin" twice), so
go by `number` and the bundle, not by name.

- **Categorise each session by its directories and title.** Use `last_dir` as well as
  `start_dir`: many sessions start in `C:\code` and move into a repo. Work
  (the machine's work folders, `C:\code\workspace`, work tickets, PRs, Slack threads)
  belongs on the Work desktop. Personal (`C:\code\personal\...`) belongs on the
  Personal desktop.
- **Group by topic into the windows already open.** Don't open a window per session:
  the user wants the existing windows, with new tabs added to them. For example, put
  the data/linking sessions into the window already running linking work.
  - Never put a session in an unrelated window, such as an SSM tunnel or a build log.
- **Open a new window only when no suitable window exists** (say, no terminal on that
  desktop). New windows open on the current desktop, so a new-window group must be
  applied from its own desktop. Give it a `place` from a free slot in the `placement`
  values of `terminals --json`.

Write the groups file:

```json
{"groups": [
  {"name": "linking", "window": 591312, "sessions": ["eb5b321d", "030a66f7"]},
  {"name": "platform", "window": 264420, "sessions": ["6d6bc997"], "reuse_idle": false},
  {"name": "personal", "window": "new",
   "place": {"screen": 3, "mode": "halves", "slot": 1, "orientation": "vertical"},
   "sessions": ["a1b2c3d4"]}
]}
```

- `window`: an `hwnd` from `terminals --json`, or `"new"`.
- `sessions`: id prefixes from `list`. A session may appear once across all groups.
- `reuse_idle` (default true): type into the window's idle tabs before adding tabs.
- `place`: new windows only. To move an open window, use
  `desktop-manager claude-sessions place <hwnd> --screen 3 --mode halves --slot 1`.

## 4. Plan

```
desktop-manager claude-sessions plan groups.json --since 72h --out plan.json
```

It prints which tab, new tab or new window each session gets, group by group, and
writes the plan. It refuses (exit 2, naming the problem) a session that is running, one
that was cleared (unless `--include-cleared`), one named twice, a window that isn't
open, and `place` on an open window. Show the user the plan before applying it.

Plan groups on different desktops into separate files, so each can be applied from its
own desktop.

## 5. Apply

```
desktop-manager claude-sessions apply plan.json [--dry-run] [--max-starting 4]
```

Before typing anything, `apply` re-checks the plan:

- **Terminals.** If a window the plan names has closed, or an idle tab has closed or is
  now busy, it stops with nothing typed and exit 1. Re-run `plan`.
- **Running sessions.** A session that is running now (resumed by hand, or by another
  agent, since the plan was made) is left alone and reported.

Then it works in three steps:

1. **Open.** Every new window and new tab the plan needs is opened first, using the
   terminal's own new-tab button so the user's default profile and colours apply. Their
   shells start up together. New windows are moved to their `place`.
2. **Type.** Each tab is selected and gets a change to the session's start directory,
   then `claude --resume <id>`. This takes focus, so it runs one session at a time, but
   each takes about a second. The live-session registry is checked again just before
   each command, so nothing gets a second writer.
3. **Verify.** One poll covers every session that is starting. At most
   `--max-starting` (default 4) are starting at once, which caps the memory spike of
   loading transcripts. Lower it for very large transcripts or a low-memory machine.
   `--stagger <s>` adds a minimum gap between typing one resume and the next.

It hands focus back at the end, prints `N/M running`, and exits 1 if any session
failed. Warn the user not to type while it runs.

### The shortcut: `recover`

`recover <id> ... [--window <hwnd>] [--dry-run]` plans and applies in one step, but
chooses the tabs itself: idle tabs first, then new tabs spread across the open windows
(`--window` keeps to one; `--max-tabs` caps a window before a new one opens). Use it for
one or two sessions. Use `plan` and `apply` whenever the grouping matters.

## 6. Verify

Run `terminals` again. A resumed tab's title changes to Claude's status glyph plus the
session title (for example `✳ …`). For each session resumed from the hung group, tell
the user what it was doing when it died, so they know what to check.

## Rules

- Never resume a session that is running: two processes would write one transcript.
  The tool refuses at plan time and checks again before typing. Don't work around it
  with a bare `claude --resume`. A session another agent reports as dead may be alive
  again, so trust `list`, not the report.
- Never open terminals from a `wt` command line or by launching `pwsh` yourself. That
  picks the wrong profile (unstyled black-and-white tabs) and the wrong window, and it
  can't reuse idle tabs.
- If `apply` or `recover` stops with an automation error, the terminal wasn't in the
  state the plan expected. Sessions already typed are still verified; nothing further
  is typed. Run `terminals` again and re-plan. Don't retry blindly.
- Claude asks "Do you trust this folder?" the first time it runs in a new directory
  (a fresh worktree, say), and registers the session only after the answer. `apply`
  reports it as "waiting at Claude's folder-trust prompt". The default answer is "No,
  exit". Leave the answer to the user; never send keys to accept it.
- A session whose start directory was deleted is skipped (`skipped` in `list --json`).
  Say so, and let the user decide whether to recreate it.
- A session resumes in the directory it **started** in, even if it moved into a
  worktree later. If `last_dir.exists` is false, the worktree it was working in is
  gone: tell the user before resuming, since the session will expect it.

## Orca tabs lost at restart

On Windows a sign-out can close Orca's agent tabs before Orca knows it is shutting down, and
Orca saves the closed state (cause and evidence: `docs/orca.md`, "Agent tabs closed by a
Windows shutdown"). The work machine runs two instances, `%APPDATA%/orca` (work) and
`%APPDATA%/orca-personal`; check both, since one often loses far more than the other.

1. **Find what each instance lost.** Read-only:

   ```
   uv run python -I <this skill's folder>/scripts/orca_lost_tabs.py --orca-data "$APPDATA/orca" [--json]
   ```

   The script ships in this skill's `scripts/` folder and needs only the standard library.

   It compares the newest `profile-state.db.backup.*.db` saved before the running Orca
   started with the live state, and lists each lost tab with its worktree, agent session id
   and an `orca terminal create` line. Sessions already open again in a tab are left out.
   Plain shells are listed without a command; leave them unless the user wants them.
2. **Triage as in step 2**, using `desktop-manager claude-sessions show <id>` for the
   session ids it prints. Confirm which to reopen.
3. **Reopen in the right instance.** From a terminal of the other instance, clear the
   binding or the CLI refuses with `runtime_source_mismatch`:

   ```
   env -u ORCA_RUNTIME_SOURCE_ID ORCA_USER_DATA_PATH="$APPDATA/orca" <printed orca terminal create line>
   ```

   Run the printed lines as given. They reopen each tab in the worktree it sat in, `cd`ing
   to the session's own folder first when it differs. That matters: `--worktree path:` only
   takes worktrees Orca has registered, so pointing it at the session's folder fails with
   `selector_not_found` when that worktree is hidden from the sidebar. Don't hand-edit the
   paths; backslashes in `--command` get mangled by shell escaping.
4. **Verify** with `orca terminal list --json` (same `env` prefix): each tab's `preview`
   should show Claude's prompt, not a `Cannot find path` error or the folder-trust
   prompt. `orca terminal read --terminal <handle>` shows more. A tab stuck at the trust
   prompt is the user's to answer. If one went wrong, `orca terminal close --terminal
   <handle> --tab` and create it again.

Orca keeps only its five newest hourly backups, so the pre-restart one is gone about five
hours after the restart and the script stops with `no backup saved before this Orca
started`. Fall back to the `session-exited` lines at the shutdown time in
`logs/daemon.log` and step 1 of the main flow.

The script only sees tabs the backup holds. Tabs opened after that backup show up as
`session-created` lines in `logs/daemon.log` after the backup's time; find their sessions
with `list` above.
