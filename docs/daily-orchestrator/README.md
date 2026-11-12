# Daily estate orchestrator

A scheduled Orca automation that checks the whole estate each weekday morning, starts an independent
Orca session for each piece of work an agent can do, and reports what needs the owner. It works the
same way as the [scheduled project reviews](../project-reviews/README.md): the automation's prompt is
a fixed [bootstrap](bootstrap.md), and the instructions live in [policy.md](policy.md) on main, changed
only through PRs.

| File | Holds |
| --- | --- |
| [policy.md](policy.md) | scope, checks, dispatch rules and cap, memory, output: what each run does |
| [bootstrap.md](bootstrap.md) | the automation's whole prompt, and how to roll it out |
| [config/orca-automations.json](../../config/orca-automations.json) | the binding: automation id, profile, trigger, timezone, agent, workspace, policy path, memory issue |

The run follows the machine's role (`tools.machine.role`: `personal` by default, or `work`). A
personal machine looks after the personal estate; a work machine looks after the work orgs in
`tools.estate.work_orgs`, and nothing personal. `uv run estate scopes` shows the role and how every
owner of the clones on the machine is classed: personal, work or other.

`uv run estate daily-triage --root C:/code --json` gathers the PR half of the findings (open PRs,
failing checks, conflicts, stale bases, unresolved threads, superseded PRs) so the agent only decides
and dispatches. Its lanes are documented in the policy and in `--help`.

## Editing

1. Change `policy.md` in a PR. Keep the schedule key, which the bootstrap checks.
2. Review what changed in scope, the roles, the dispatch table and the cap: those are what
   widen or narrow what an unattended run may start.
3. Squash merge. The next run fetches the new main; nothing else to do.

Changing the schedule, the workspace, the policy path or the memory issue also needs the manifest
updated and the automation reconciled by hand ([bootstrap.md](bootstrap.md#rollout)). Scheduled runs
never edit the policy, the manifest or the automation.

## Memory

The policy uses one canonical rig issue as the run ledger, the same shape as the review issues, and
Orca's own worktree comments (`daily-orchestrator:<run-id>:<item-id>`) as the duplicate guard. The
issue is chosen over the rig jobs database because it survives a machine move, a session can read it
with `gh` from any checkout, and the owner can read and correct it. Until one is created and set as
`memory_issue` in the manifest, runs fall back to Orca metadata alone.
