# C# port

[Issue #85](https://github.com/rtkelly13/rig/issues/85) tracks the migration under ADR 0022. The source port is complete (#285): rig is C#, and `tools/image-drift` is the one Python project.

| Guide | Read when |
| --- | --- |
| [ownership.md](ownership.md) | Porting or deleting a command: inspected ownership ledger, CI refusals, exact duplicate baseline, rollout inventory and named platform acceptance procedures |
| [acceptance.md](acceptance.md) | Recording or reviewing acceptance for a ported capability: one row per capability/platform with owner, PR, state, evidence and the still-pending native procedure |
