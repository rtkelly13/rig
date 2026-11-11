# Wrap-up

**Up:** [`estate`](README.md) · [Orca](./orca.md) · [Worktrees](./worktrees.md) · [Shelving](./shelving.md) · [Decision feed](./decisions.md)

`estate wrapup` asks idle Orca agent sessions whether their change has reached its natural end, and
what is still left for you to do: PRs to open or merge, issues to file, and pushes still to make. Each
agent records its own answer by running `rig estate wrapup report`, after whatever checks it needs.
`--apply` hands the asking to a background follower and returns straight away. The follower asks five
sessions at a time, and as each one answers it asks the next, until every idle session has been asked.
Run it again without `--apply` to read the answers.

```bash
uv run estate wrapup --personal --all                    # preview: who would be asked, and why not the rest
uv run estate wrapup --personal --all --apply            # ask them all in the background, 5 at a time
uv run estate wrapup --personal --all                    # later: read each verdict
uv run estate wrapup --personal -r rig --show-prompt     # see the exact text each session would get
uv run estate wrapup --personal -w dragonfish --children # one worktree and its subtasks, across repos
uv run estate wrapup --work --all --json                 # work repos, machine-readable
```

## Targeting

| flag | meaning |
|---|---|
| `--personal` / `--work` | **required**, exactly one. Personal covers `personal/` and the org folders in `config/estate-scopes.json`; work covers this machine's `tools.estate.work_dirs` in `~/.workspace/config.json`, and selects nothing when none are set |
| `--repo/-r NAME` / `--all` | **required**, one or the other. `--repo` takes the Orca repo name, is case-insensitive and repeatable |
| `--worktree/-w NAME` | narrows to worktrees matched by display name, folder, branch or path. On its own, it searches the whole scope |
| `--children` | also takes in each chosen worktree's Orca subtasks (child worktrees), recursively and across repos, but never across scope |
| `--idle 30m` | asks only sessions that have been `done` at least this long |
| `--max 5` | how many sessions are asked at once. With `--wait 0`, how many are asked per run; the rest show as `deferred` |
| `--wait 10m` | with `--apply`, how long each asked session gets to answer before the next is asked anyway. `0` asks `--max` in the foreground and stops |
| `--follow` | with `--apply`, asks in the foreground and prints progress, instead of starting a background follower |
| `--include-settled` | also asks worktrees the facts already show as finished |
| `--template PATH` | uses a different prompt file |

Scope comes from the folder of the worktree's **repo**, not of the worktree, because Orca can create
worktrees outside the code root. This Mac runs one Orca profile for both kinds of work, so the path is
the only signal. On Windows the two are separate Orca profiles, and `wrapup` still reads only the one
the `orca` CLI on `PATH` talks to.

## What gets asked, and what doesn't

Only top-level Claude panes are considered. Sub-agent panes and other agent types are skipped. Each
session gets one status:

| status | meaning |
|---|---|
| `ask` / `sent` | idle, with a live Claude terminal, and not settled. `sent` once `--apply` delivered it |
| `deferred` | with `--wait 0`, would be asked but `--max` was reached; otherwise, not asked because the run was interrupted |
| `settled` | the facts already prove it finished: no uncommitted changes, nothing on no remote, and the PR merged or closed (or, with no PR, the branch merged into the default branch) |
| `answering` / `answered` | already asked. `answered` shows the parsed verdict |
| `working` / `waiting` | busy, or blocked on a question to you (the question is shown). Never sent anything |
| `recent` | idle for less than `--idle` |
| `no-terminal` | no live, writable terminal that Orca recognises as Claude. A plain shell is never typed into |
| `send-failed` | Orca refused the send. The command exits 1 |
| `unconfirmed` | the state file says it was asked, but the pane hasn't changed since: the prompt never arrived. Not asked again until the pane moves; look at it |

The settled check is the deterministic catch. It uses the same git checks as `tidy` and `decisions`
(`is_dirty`, commits on no remote, merged by ancestry), so worktrees that are clearly finished cost no
agent turns.

## Asking a few at a time

`--apply` prints the plan, starts a follower in the background and returns. The follower is the same
command with `--follow`; its output goes to a log under rig's app data folder
(`logs/wrapup/follow-<time>.log`), and the command prints the path. Only one follower runs at a time:
a second `--apply` while one is asking exits 1 and starts nothing. Pass `--follow` yourself to watch
it in the terminal.

The follower keeps at most `--max` sessions have an unanswered prompt at once. Rig polls
`orca worktree ps` every 5 seconds. A slot frees up when a session:

- **reports**: its `rig estate wrapup report` lands in the worktree's state file. This frees the slot
  even while the agent is still finishing its turn;
- **answers**: the `[Wrap-up check]` prompt is its last prompt, and it went idle after the send. Its
  verdict is written to the state file straight away;
- **starts waiting on you**: a question or a permission prompt. It is shown as `waiting`, since only
  you can clear it; or
- **has had `--wait`**. It stays `sent`, and a later run reads its answer.

Then the next session is asked. Subtasks go first, and a parent is held until all of its subtasks
are done, so `$children` in its prompt carries their fresh verdicts. Progress lines go to stderr, so
`--json` output stays clean. Ctrl-C stops a `--follow` run: whatever was sent is already recorded, and
the rest show as `deferred`.

Rig watches both the state file and Orca: a report shows up in the file, and the reply fallback shows
up only in Orca.

## Reporting

The prompt tells the agent to run one of:

```bash
rig estate wrapup report --ask <id> --done
rig estate wrapup report --ask <id> --pending "merge PR #9" --todo "merge PR #9" --todo "close #4"
```

from inside its worktree. It writes an answer with `via: "report"` against the ask with that id, so
the agent can take as many tool calls as it likes first, and the verdict is never parsed out of prose.
Without `--ask` it answers the newest unanswered ask in the worktree. It exits 1, writing nothing,
when there is no ask to answer or the state file can't be written; the agent then falls back to the
reply line below.

The prompt names rig by the absolute path of the `rig` that sent it, because an agent's shell in
another repo has neither this repo's virtualenv nor your shell functions. `desktop-manager runtime
install` also puts `rig` on `PATH` ([runtime](../desktop-manager/runtime.md#rig-on-path)), so `rig
estate wrapup report` works anywhere once the runtime is installed.

A report wins over a reply: once an ask has an answer, a `WRAPUP:` line in the session's reply is not
recorded over it.

## The prompt

`config/orca-wrapup.md` is the template. Edit the wording freely; `$repo`, `$worktree`, `$branch`,
`$facts` (the facts line from the table), `$children` (each subtask with its facts and verdict) and
`$report` (the quoted report command, with this ask's id) are filled in. Unknown `$names` are left as
they are.

Two parts are load-bearing:

- **The `[Wrap-up check]` marker.** rig adds it in front of the text. Orca remembers each session's last
  prompt, so while the marker is that prompt, the last reply is the answer. Once you type something
  else the marker is gone, and the state file below takes over.
- **The `WRAPUP: done` / `WRAPUP: pending: …` line**, the fallback when the report command fails. rig
  reads the verdict from the first such line in the session's last reply. Markdown around it is
  tolerated. A reply with neither a report nor this line is shown with a note.

Sending uses `orca terminal send --enter --wait-submit 10`, so a send counts only once the agent has
accepted the text.

## The state file

Each worktree keeps its wrap-up history in `temp/.rig/wrapup.json`, under rig's
[worktree state standard](../repo-governance/worktree-state.md). If the file is missing, the worktree
has never been asked. rig writes it only where git ignores it. Elsewhere the row says
`state not recorded`, and wrap-up falls back to the marker alone.

```json
{
  "schema": "rig.wrapup/1",
  "worktree_id": "<Orca worktree id>",
  "branch": "rtkelly13/orca-wrapup",
  "asks": [
    { "at": "2026-11-07T12:00:00.000+00:00", "pane": "<tabId>:<leafId>", "session": "<conversation name>",
      "head": "<sha>", "facts": "clean; PR #9 open, checks success", "template_sha": "<12 hex>",
      "id": "<8 hex>" }
  ],
  "answers": [
    { "at": "2026-11-07T12:20:00.000+00:00", "pane": "<tabId>:<leafId>", "verdict": "pending",
      "detail": "merge PR #9", "head": "<sha>", "asked_at": "2026-11-07T12:00:00.000+00:00",
      "todo": ["merge PR #9"], "via": "report" }
  ]
}
```

- **Asks** are appended only for prompts `--apply` actually delivered. `template_sha` shows which wording
  was sent; `head` and `facts` show what the worktree looked like at the time.
- **Answers** are appended by `report` (`via: "report"`, with its `--todo` items), and whenever a run
  sees a `WRAPUP:` verdict in a reply that isn't recorded yet (`via: "reply"`), **with or without
  `--apply`**. This is the one write a preview makes: it's gitignored bookkeeping, not a change to
  the estate. `asked_at` links the answer to its ask; it is `null` when the session was asked before rig
  kept a record.
- Both lists only grow. A pane's latest ask and latest answer are its last entries.

The [triage inbox](../triage.md#action-signals-and-groups) reads these files too. Each open PR shows
its worktree's newest verdict as a chip (*Agent: done*, *Agent: merge PR #9*, *Agent asked, no
reply*), and you can filter on it under *Needs action*.

How a later run uses the latest ask for a pane:

| Orca shows | status |
|---|---|
| an answer is recorded for the ask | `answered`, whatever the pane is doing |
| the marker is still the last prompt | `answering` / `answered`, as above |
| no marker, and the pane changed state after the ask | you carried on working: judged afresh (idle, settled, ask). The last recorded answer is shown in the note |
| no marker, and the pane hasn't changed state since the ask | `unconfirmed`: the prompt never arrived |

## Where it runs

`estate wrapup` and `estate wrapup report` run on the C# CLI. The command is in
`src/Rig.Cli/Estate/WrapupCommand.cs`; the plan and sends in `src/Rig.Core/Estate/Wrapup.cs`; the rolling
follow and the follower in `WrapupRun.cs`; the state file in `WrapupState.cs`. The only Python left is
`rig.estate.wrapup_state`, which the triage inbox uses to read the state file.

The follower holds `wrapup/follow.lock` under rig's app data folder exclusively: a share-deny open on
Windows, an advisory lock on macOS and Linux. Either is released when the process exits.
