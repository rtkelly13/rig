# AGENTS.md

Shared development utilities — desktop workspace management, general tooling, and reusable patterns.

> [!IMPORTANT]
> **Agent Directive**: Always run `rig help` at the start of every session to familiarize yourself with the available utility toolkit and latest command structures. It prints the `rig` overview; `uv run desktop-manager --help` lists the desktop tools. Inside a rig checkout, `dotnet run --project src/Rig.Cli -- <command>` runs that checkout's code instead of the installed copy.

## Commands

```bash
uv sync                          # install
dotnet run --project src/Rig.Cli -- status          # git state, lint, types, security (no tests)
dotnet run --project src/Rig.Cli -- status --tests  # the same plus the test suite
uv run pytest tests/test_file.py -k "test_name"
uv run ruff check src/ && uv run ruff format src/
uv run basedpyright && uv run ty check src packages/type-ratchet/src
uv run ratchet                   # type-soup ratchet; see docs/python-types.md
uv run lint-imports              # import contracts (layers, cycles, independence)
pnpm dlx @ls-lint/ls-lint        # filename and script-language lint (bans .sh and .ps1)
dotnet build rig.slnx -m:2       # C# side (ADR 0022); keep -m:2 locally
dotnet test --solution rig.slnx  # C# tests (xUnit v3 on Microsoft.Testing.Platform)
```

Every CLI's `--help` documents its flags, findings and defaults: read it before the docs.

[docs/development.md](docs/development.md) has the rest: pytest markers and which tests run where,
the Windows-safe test rules, every quality gate, and the Tailwind gallery build. Read it before
writing tests. Shared test doubles live in `tests/fakes/` (one per port); reuse them
([docs/testing.md](docs/testing.md#shared-fakes)).
Read [docs/python-types.md](docs/python-types.md) before writing Python in `src/`:
basedpyright is strict with `Any` banned, and the ratchet blocks the shapes it lists.

## Working Rules

- Start from existing repository conventions and commands.
- Keep edits minimal and avoid broad refactors unless the task requires them.
- Only the repository root should define `AGENTS.md`; do not add duplicate instruction files under `.github/`.
- Never revert unrelated user changes in the worktree.
- Prefer targeted tests, lint, or type checks over broad full-repo runs when scope is small.
- Prefer DuckDB for local analytics and for cross-referencing large JSON feeds instead of repeating API calls.

## Web Research and Review

- Read local code and docs first; go to the web only when the answer is not in the workspace.
- Prefer authoritative sources (official docs, vendor references), summarise rather than paste,
  and call out where external guidance conflicts with the current implementation.
- In review, focus first on correctness, regressions and operational risk, then missing
  validation, missing tests and unclear configuration. Say so when only scoped files were touched
  in a repo with heavy unrelated churn.

## Where to look

[docs/README.md](docs/README.md) indexes every guide by subject. The ones that carry rules:

- **Target architecture**: [docs/target-architecture.md](docs/target-architecture.md) is the end state rig
  converges on (desired → plan → apply, one port per external state, behaviour tests through seams).
  Structural changes cite an item in the #30 plan.
- **Browser automation**: use `agent-browser` with the dedicated **dev** profile; never auto-connect
  to or import daily Chrome sessions. [docs/agent-browser/README.md](docs/agent-browser/README.md).
- **Scheduled ChatGPT reviews**: policy lives in [docs/project-reviews/](docs/project-reviews/README.md)
  and changes only through PRs. Workers never grade themselves or edit policy;
  [evaluations.md](docs/project-reviews/evaluations.md) is the operator's process.
- **Daily estate orchestrator**: the Orca automation's policy lives in
  [docs/daily-orchestrator/](docs/daily-orchestrator/README.md) and changes only through PRs; its
  prompt is the fixed bootstrap there.
- **Brand assets**: [docs/brand-assets/README.md](docs/brand-assets/README.md) is authoritative.
  The traced master changes only through `image-reconcile promote`, and cuts derive only from
  `production-cuts.csv`.
- **Vercel infra**: [infra/vercel/README.md](infra/vercel/README.md). `sites.ts` is the registry,
  and anything already serving traffic is `adopt`, never `manage`.
- **Git technique**: [docs/git/README.md](docs/git/README.md); stacking, splitting, landing, conflicts.
- **Estate and packages**: [docs/estate/README.md](docs/estate/README.md) and
  [docs/packages/README.md](docs/packages/README.md).
- **Command policy**: [docs/agents/command-policy.md](docs/agents/command-policy.md); what an agent
  may run unprompted ([source of truth](config/cli-allowlists.json)).

## Moved repository and pending machine setup

`rtkelly13/rig` was restarted without history on 2026-11-07, so that work content never ships with
it. The old history, issues closed before the move and every earlier PR stay read-only in
[`rtkelly13/rig-archive`](https://github.com/rtkelly13/rig-archive).

- **Never push a branch carrying the old history here.** A clone from before the move has
  unrelated history: fetch the new `main` and rebase each branch onto it with
  `git rebase --onto origin/main <old main> <branch>`. The trees match, so the rebase is clean.
  Moving a whole clone (guard, audit, port, teardown):
  [docs/git/moving-a-clone-across-a-history-restart.md](docs/git/moving-a-clone-across-a-history-restart.md).
- **Machine-specific values live in `~/.workspace/config.json`, not in the repo**: the work folders
  (`tools.estate.work_dirs`), the work workspace checkout (`tools.estate.workspace_repo`) and desktop
  plan aliases (`tools.desktop.aliases`). See [docs/estate/README.md](docs/estate/README.md) and
  [docs/desktop-manager/plan-files.md](docs/desktop-manager/plan-files.md).
- **The Windows machine has not been moved yet.** Its steps are in the
  [handoff comment on rig-archive#647](https://github.com/rtkelly13/rig-archive/pull/647#issuecomment-6714357821).
  Where the comment says to check out `rtkelly13/orca-wrapup`, re-point the clone at this repo
  instead (above). Remove this bullet once that machine is done.

## 🛑 Repository Conventions & Workflow Policy

1. **Squash Merge Only**: All pull requests must be merged into `main` using **Squash and Merge** exclusively.
2. **Delete Branch on Merge**: Feature branches must be automatically deleted immediately upon merge into `main`.
3. **Linear History**: Maintain a strictly linear history. Rebase feature branches onto `main` before merging; no merge commits allowed.
4. **Direct Push Protection**: Non-force direct pushes to `main` are blocked; PR mechanism required (force pushes permitted when needed).
5. **Local Temp & Worktree Directory**: All temporary files, local databases, and scratch files MUST go inside the root `/temp/` directory; git worktrees go in `/temp/` or `/.worktrees/` (both gitignored).
6. **Gitignored Local TODO File**: A root `TODO.md` file MUST exist for local task tracking and be gitignored.
7. **Auto-Merge Enabled**: PRs may enable auto-merge (squash) so they merge automatically once required checks pass.
8. **Pinned Action SHAs**: All GitHub Actions workflows MUST use 40-character commit SHAs instead of mutable tags.
9. **Script Language Policy**: Automation scripts in `scripts/` MUST use the repository's approved language stack (e.g., C# `.cs` for .NET, TypeScript/JavaScript for Node, Python `.py` for Python). Alien scripting runtimes (e.g., `.py` in C# repos, `.sh`/`.ps1` everywhere) are strictly prohibited and gated by CI.
