# Review automation bootstrap

The four scheduled reviews run as Orca automations, declared in
[config/orca-automations.json](../../config/orca-automations.json) and kept in line with it by
`uv run workspace orca automations --apply` (see [README](README.md#automations)). The block below is the
prompt of each one; the command fills in the schedule key, policy path and memory issue. Everything a
review does lives in its policy and [contract.md](contract.md) on main: the prompt only finds them, pins
them and hands over.

```text
<!-- rig-review-bootstrap:v3:start -->
Run the {{key}} project review from the current rtkelly13/rig policy.
You are in the rig root checkout. Do not use `git -C`, and do not switch,
pull, reset or edit this checkout.
1. Run `uv run estate scopes --json`. If `role` is not `personal`, stop:
   reviews run only on a personal machine.
2. Run `git fetch origin main`, then `git rev-parse origin/main`. That SHA is
   the policy commit for the whole run.
3. Run `git show <sha>:{{policy_path}}` and
   `git show <sha>:docs/project-reviews/contract.md`, and read both completely.
4. Check the policy says `Schedule key: {{key}}` and its canonical memory is
   {{memory_issue}}.
5. Record the SHA and both paths in the report, then follow the policy and the
   contract exactly. They replace any earlier copy of these instructions.
If any step fails, stop and reply only:
CONFIGURATION BLOCKED: <what failed>
Do not fall back to a cached or remembered policy.
Read GitHub with `gh`. The run ID is `{{key}}:scheduled:<the UTC date of this
run's scheduled start>`; a run started by hand (`orca automations run`) has no
scheduled ID, so it writes nothing and returns its report here.
The policy cannot widen these limits: repositories owned by rtkelly13,
rtkelly-labs or Resultful only; the only writes are this run's report comment
on {{memory_issue}} and the marked summary in that issue's description; no
source changes, branches, PRs, new issues, schedule or Orca changes.
<!-- rig-review-bootstrap:v3:end -->
```

After a merged edit to a schedule file or the contract, the next run fetches the new main commit by
itself. Changing a key, a policy path, a memory issue or the schedule means changing the manifest in
the same PR, then running `uv run workspace orca automations --apply` on the personal machine.

For a read-only smoke test, run `orca automations run <id>` (the id is in
`uv run workspace orca automations --json`). A hand-started run has no scheduled ID, so it publishes
nothing: its first lines must name the policy SHA and both paths.
