# Docs

Each folder has a README saying what is in it and when to read each part. Command flags, defaults
and findings are in each CLI's `--help`; these guides hold what `--help` can't.

## By subject

| Folder | Covers |
|:-------|:-------|
| [agents/](agents/README.md) | `AGENTS.md` rules and audit, the command policy, the skills catalog |
| [repo-governance/](repo-governance/README.md) | GitHub-side conventions: required checks, rulesets, policy config, ls-lint, Vercel gating, org and apps |
| [open-source/](open-source/README.md) | Running public repos: the expectations hub, OSS automation, review tooling, upstream bug reports |
| [packages/](packages/README.md) | Publishing to npm and NuGet: package audit, trusted publishing, new-package runbooks |
| [estate/](estate/README.md) | The clones on this machine: disk, worktrees, shelving, caches, Orca's sidebar |
| [git/](git/README.md) | Stacking, splitting, landing and conflict techniques |
| [desktop-manager/](desktop-manager/README.md) | Windows workspace restore, runtime copies, work items, Chrome tabs |
| [agent-browser/](agent-browser/README.md) | Browser automation profiles, connection approaches, ChatGPT Work schedules |
| [project-reviews/](project-reviews/README.md) | Live scheduled-review policies and their evaluation |
| [brand-assets/](brand-assets/README.md) | **Authoritative** for brand asset layout, thresholds and the promotion gate |
| [vault/](vault/README.md) | Maintaining the Obsidian vault with the `vault` CLI |
| [adr/](adr/) | Architecture decision records |
| [superpowers/plans/](superpowers/plans/README.md) | Implementation plans, kept as history |

## Single guides

| Guide | When to read |
|:------|:-------------|
| [target-architecture.md](target-architecture.md) | Before restructuring code: the end state rig converges on, its layers and how they are enforced |
| [development.md](development.md) | Before writing tests or touching CI: markers, Windows-safe test rules, every quality gate |
| [testing.md](testing.md) | Choosing BDD, property or example tests, and measuring test quality with coverage and mutation score |
| [test-quality.md](test-quality.md) | What the research says about measuring test quality, and the scorecard for each type of test |
| [python-types.md](python-types.md) | Before writing Python in `src/`: the banned shapes the type ratchet blocks and their replacements |
| [feature-flags.md](feature-flags.md) | Switching a feature on or off across rig tools: `rig flags`, defaults and overrides in `rig.db` |
| [web-routes.md](web-routes.md) | Serving JSON or a page from `rig web`: the `Route` protocol, error envelope, write guard and composition root |
| [observability.md](observability.md) | Adding or reading traces from the CLI tools |
| [otel-stack.md](otel-stack.md) | The always-on local OTLP collector on 4317/4318: install, startup job, 7-day files, forwarding |
| [workload.md](workload.md) | The 15-minute memory sampler: what it records, where, the 7-day retention, cost, and reading a day back |
| [mail.md](mail.md) | Gmail triage: where the untracked rules, their Drive backup and the Apps Script live; recovery |
| [forks.md](forks.md) | Managed forks: a repo tracking an upstream we don't own, with our changes on top |
| [triage.md](triage.md) | `rig triage`: the estate-wide PR and issue queue and its `rig web` page |
| [web-ops.md](web-ops.md) | The Operations pages under `/ops` in `rig web` |
| [orca.md](orca.md) | `workspace orca doctor/sync`: agent launch flags, worktree folders and per-repo setup |
| [obs-recording.md](obs-recording.md) | Recording a live talk with OBS |
| [bootable-usb-windows.md](bootable-usb-windows.md) | Rewriting an Ubuntu LTS install stick from Windows |
