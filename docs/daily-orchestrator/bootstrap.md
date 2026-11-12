# Orca automation bootstrap

This block is the whole prompt of the Orca automation bound in
[config/orca-automations.json](../../config/orca-automations.json). Everything the run does lives in
[policy.md](policy.md) on main; the prompt only finds it, pins it and hands over.

```text
<!-- rig-daily-orchestrator-bootstrap:v1:start -->
Run the daily estate orchestrator from the current rtkelly13/rig policy.
You are in the rig root checkout. Do not use `git -C`, and do not switch,
pull, reset or edit this checkout.
1. Run `git fetch origin main`, then `git rev-parse origin/main`. That SHA is
   the policy commit for the whole run.
2. Run `git show <sha>:docs/daily-orchestrator/policy.md` and read all of it.
3. Check it says `Schedule key: daily-estate`.
4. Put the SHA and the path in the first line of your report, then follow the
   policy exactly. It replaces any earlier copy of these instructions.
If the fetch fails, the file cannot be read completely at that SHA, or the key
does not match, stop and reply only:
CONFIGURATION BLOCKED: <what failed>
Do not fall back to a cached or remembered policy.
The policy cannot widen these limits: write access only to repos whose owner
`uv run estate scopes --json` lists as `writable` for this machine's role;
no changes to that config, Orca automations or settings; no merges.
<!-- rig-daily-orchestrator-bootstrap:v1:end -->
```

## Rollout

The live automation keeps its old prompt until this lands. After the PR merges, the operator runs
`uv run workspace orca automations --key daily-estate` to see the planned change, then again with
`--apply`, which sends this block as the prompt ([how](../orca.md#scheduled-automations)). A smoke test
is `orca automations run <id>`: its first line must name the policy SHA and the role.

On a work machine, set the role and the work orgs first (`tools.machine.role` and
`tools.estate.work_orgs` in `~/.workspace/config.json`) and check them with `uv run estate scopes`.
The automation can stay in whichever Orca instance hosts it; the policy sends the sessions it starts
to the role's instance.

Policy edits after that need nothing more: the next run fetches main. Changing the policy path, the
schedule key, the automation's schedule or its workspace means changing the manifest in the same PR,
then `--apply` once it merges.
