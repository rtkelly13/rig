# rig Simplification Stack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move rig to the end state in [docs/target-architecture.md](../../target-architecture.md):
- one shape per feature: desired → plan → apply;
- one port per kind of external state, each with a shared fake;
- an acyclic, layered import graph enforced in CI;
- behaviour-level tests.

Along the way, fix the correctness and security findings and remove about 14–15k lines of dead or duplicated
behaviour.

**Architecture:**
1. Build three leaf seams first: `rig/roots.py`, `RepoSlug` and `rig/proc.py`. Every later chain depends on them.
2. Delete dead behaviour in parallel; most desktop_manager import cycles disappear with it.
3. Introduce the desktop and GitHub ports, each with one fake and a behaviour suite.
4. Lock everything in with import-linter, Ruff `banned-api`, ratchet rules over `tests/`, deptry and zizmor.

Every gate lands in three steps:
1. Add the contract with today's violations listed as explicit ignores.
2. Migrate the code.
3. Delete the ignores.

**Tech Stack:** Python 3.13, uv, Typer, pydantic, basedpyright strict, ty, Ruff, type-ratchet, pytest,
import-linter 2.15, platformdirs, inline-snapshot, zizmor, pinact, Dependabot.

**Spec:**
- `docs/target-architecture.md` is the end state.
- `TODO.md` is the itemised findings, with `file:line` evidence. It is gitignored and worktree-local, so copy it
  to the main checkout before this worktree is removed.
- `temp/evaluation/` (also worktree-local) holds the full evaluation, `cycles.txt` and `importlinter.ini`.

## Tooling evaluation (measured on this repo, 2026-10-29)

"py.typed" was checked by installing each package. "Measured" means it was run against this repo.

| Tool | Verdict | Use | Evidence |
|---|---|---|---|
| **import-linter 2.15** (grimp 3.18) | **Adopt** | `layers`, `acyclic_siblings`, `forbidden` and `independence` contracts; `ignore_imports` for staged adoption | Measured: 2 broken contracts. `rig → desktop_manager` at 4 sites; `rig` needs 2 cuts and `desktop_manager` 11 to become acyclic. grimp's `nominate_cycle_breakers` gives the cut list. Ships py.typed. It replaces the custom `package-cycles` ratchet rule that was proposed earlier. |
| Tach 0.35 | Reject | — | Overlaps import-linter. The extra it offers, public-interface checks, is already covered by basedpyright's private-usage rules. One tool is enough. |
| Ruff `banned-api` (TID251) | **Adopt** (extend) | `subprocess` owned by `rig/proc.py`; `ctypes`/`comtypes` owned by `win32/` | Already used for the Windows modules. The ratchet's `noqa` ban means it can't be suppressed inline. |
| type-ratchet | **Extend** | Per-path rule sets; rules over `tests/` (fakes location, no `mock.patch`/`monkeypatch.setattr`/`sys.modules` injection); path-owned primitives (`Path(__file__).parents`, `LOCALAPPDATA`, `/Users/`) | It's the house gate: stdlib-only and zero-baseline. |
| platformdirs | **Adopt** | App-data directories (5 hand-rolled resolvers today) | py.typed ✔ |
| inline-snapshot | **Adopt** (already a dev dep, currently unused) | Plan and JSON snapshots in behaviour suites (devenv, governance plan, restore dry-run) | py.typed ✔ |
| pytest-bdd | **Drop** | Use typed given/when/then step functions in plain pytest | Ships py.typed, but step parameters arrive as parsed strings, and the single existing `.feature` test needed `dict[str, object]`, which the ratchet bans. Duplicate step text shadows silently instead of erroring. |
| pytest-mock | **Drop** | — | Unused. Patching is being designed out. |
| hypothesis | Defer | Property tests for pure parsers and planners (porcelain parsers, SVG path parser, `plan → apply → plan` is empty) | py.typed ✔. Worth it once those parsers are each in one place. |
| syrupy | Reject | — | inline-snapshot is already a dependency. |
| deptry | **Adopt** after config | Dependency hygiene gate | Measured: 113 issues today, mostly module-name mapping noise (PIL, yaml, frontmatter, win32*). Needs `package_module_name_map`. |
| pylint `duplicate-code` | Report-only | Occasional sweep | Measured: 11 blocks at 8 lines or more. The byte-identical `_git` helper pairs are shorter than that, so primitive ownership is the real control. |
| vulture | Reject as a gate | — | Measured: 347 hits, mostly Typer commands. The AST orphan scan in `temp/evaluation/orphans.txt` is more precise. |
| zizmor | **Adopt** | CI gate on `.github/workflows` | Measured (offline): 22 `unpinned-uses`, 8 `artipacked` and 5 `excessive-permissions`. This violates the repo's own SHA-pinning policy. |
| pinact + Dependabot `github-actions` | **Adopt** | Pin once, keep pins current | Replaces `action_sha_updater` and `action_shas.json`, whose map jumps `checkout@v2–v4` to v7. |
| githubkit | Defer | Typed GitHub client (py.typed ✔) | It would delete most of the 69 gh wire models, but it reverses ADR 0006 (`gh` CLI). Revisit after `FakeGitHub` exists. |
| Pulumi `github` provider | Defer | Repository settings, rulesets, labels, environments, Pages | Fits ADR 0007. Only after policy has moved from Python into config (chain E). |
| GitPython / pygit2 / dulwich | Reject | — | Half the calls are `gh`; porcelain output is already minimal; LFS and worktree parity. |
| OpenTelemetry SDK / structlog | Reject | — | `tracing.py` already writes OTLP-JSON that DuckDB queries. The SDK has no file exporter. |
| pywinauto / uiautomation | Reject | — | No py.typed. The repo already stubs comtypes. |
| svgelements / svgpathtools | Reject | — | Typing unverified. `ast_parser` is 180 typed lines. |
| APScheduler | Reject | — | `jobs/` is correctly a short-lived Task Scheduler runner, and APScheduler needs a resident process. |
| Brewfile + `winget configure` | **Adopt** | devenv tool installs and fonts | Replaces about 650 lines of catalogs and providers. |
| chezmoi / nix-darwin / mise | Reject | — | devenv's remaining work is structural JSON merges, not templates. nix-darwin doesn't run on Windows. |
| `gh pr checks --required` + `mergeStateStatus` | **Adopt** | PR readiness | Replaces `verify-pr-checks`, which has failed PRs whose checks were all green. |
| Org `.github` repository | **Adopt** | Default community health files for org repos | Shrinks `sync-templates` to LICENSE and user-owned repos. |
| copier / cruft | Reject | — | Audit-and-fill on existing repos doesn't match copier's answers-file model. |

## Global Constraints

- **Every PR is independently green:** basedpyright strict, ty, ratchet, ruff (check and `format --check`),
  ls-lint, tests. A gate never lands before its migration, and it never flips in the same PR that migrates the
  last violator, so either PR can be reverted cleanly.
- **Small PRs.** Aim for a few hundred lines each.
- **Stacking.** Name each PR's base explicitly. Squash-merge and linear history mean every PR above a merged one
  is restacked; follow `docs/git/stacked-pull-requests.md` (managed stacks merge only via `merge-async`, and
  `pending` is not success).
- **Branch naming.** Use `<issue-number>-<slug>`, so `todo status` can match branches to issues.
- **Windows CI.** Any PR touching `desktop_manager/win32/`, the restore port or Windows adapters must have
  `windows` or `win32` in the branch name or title, or carry the `ci:windows` label. Otherwise the Windows
  runner never sees it. Nothing in chain D can be verified on macOS.
- **Clean deletions.** A deletion PR also removes the code's tests, its docs mentions and its config residue:
  `loose-dict-allow` entries, Ruff per-file-ignores, `[project.scripts]`, `RETIRED_TASKS`, and the
  managed-projects or skills config.
- **Owner calls.** Archive the YouTube subsystem rather than delete it. Warp settling, the Chrome extension and
  githubkit stay out of this plan.
- **Releases.** No release or version cut while the stack is open.

---

## Chain 0: Land the target (base `main`)

### PR 0.1: Target architecture and plan
**Files:** Create `docs/target-architecture.md` and this plan. Modify `AGENTS.md` (link line).
- [ ] Commit the already-written doc, the plan and the AGENTS.md link. No code changes.

## Chain A: Standalone correctness fixes (each PR based on `main`, all in parallel)

Each PR is independent. Check off TODO §1 items as they merge.

### PR A1: Agent permissions have one source
**Files:** `src/rig/devenv/steps/ai_tools/claude_code.py`, `opencode.py`, `tests/devenv/test_ai_tools.py`
- [ ] Write a failing test: `devenv ai` produces Claude permissions equal to `workspace policy sync`'s projection,
  with no `Bash(git:*)`, `curl`, `pkill` or `python:*`.
- [ ] Delete the hard-coded baseline (`claude_code.py:30-63`) and the opencode duplicate. Call the policy projection instead.

### PR A2: devenv dry-run and agent mode are side-effect free
**Files:** `src/rig/devenv/steps/git.py`, `src/rig/devenv/engine/runner.py`, tests
- [ ] Tests: `--dry-run` writes no config and runs no `ssh-keygen`; agent mode never prompts on failure.
- [ ] Guard `save_user_config` and `ssh-keygen` (`git.py:115-130`). Make `apply_conditional_git_includes` report what it would do (`:32`). Skip `Confirm.ask` in agent mode (`runner.py:56`).

### PR A3: Asset gates have no bypass
**Files:** `src/rig/image_drift/cli_gallery.py`, `server.py`, tests
- [ ] Delete `build-preview --lock-winner` (`cli_gallery.py:400,682`).
- [ ] Validate `req.project` against known projects and drop the hard-coded `/Users/…` write (`server.py:640-648`). Chain C5 removes the endpoint entirely; this PR closes the hole now.

### PR A4: Workflow supply chain
**Files:** `.github/workflows/*.yml`, create `.github/dependabot.yml`, `ci.yml`
- [ ] Pin every `uses:` to a 40-char SHA with `pinact run`.
- [ ] Fix `artipacked` (`persist-credentials: false`) and `excessive-permissions` (least-privilege `permissions:` per job).
- [ ] Add Dependabot `github-actions` (weekly) and a `zizmor --offline` step in CI.

### PR A5: Required checks live in config
**Files:** `src/rig/repo_governance/github_rulesets.py`, `config/repo_policies.json`, tests
- [ ] Test: rig resolves its real checks (`lint`, `test`, `ci`) from config.
- [ ] Move `REPO_REQUIRED_CHECKS_MAP` (`github_rulesets.py:29-70`) into `repo_policies.json`; delete the map; keep the check-name guard.

### PR A6: Rulesets only
**Files:** `github_rulesets.py:350-440`, `cli_repo_checks.py:381-452`, tests
- [ ] Delete classic branch protection and its "Upgrade to GitHub Pro" special cases. Test: enforcement goes through the check-name guard on every path.

### PR A7: Deterministic SVG pipeline
**Files:** `src/rig/image_drift/simplifier/syntactic.py`, tests
- [ ] Remove the `npx -y svgo` pass, or pin svgo in `package.json` and fail loudly when it's missing. Delete the test that can't fail.
- [ ] Fix `geometric.simplify_subpath` handling of relative `c`/`l` commands, with a table test.

### PR A8: Retire the action-SHA pinner (based on A4)
**Files:** `repo_governance/action_sha_updater.py`, `action_shas.json`, `repo_hygiene.py:150-232`, `.github/workflows/action-sha-governance.yml`, tests
- [ ] Delete them. Document Dependabot, pinact and zizmor as the replacement in `docs/repo-governance/` (split into topic guides on main by #346).

## Chain B: Leaf seams and process runner (sequential, base `main`)

This chain closes TODO §1 items 1–2 and unblocks chains D, E and F.

### PR B1: `rig/roots.py`
**Files:** Create `src/rig/roots.py` and `tests/test_roots.py`. Modify `repo_governance/repo_discovery.py:29`, `workspace/engine.py:26,47-55`, `estate/locate.py:18`, `estate/orca.py:49`, `desktop_manager/work/git_facts.py:47`.

**Interfaces:**
- `code_root() -> Path`: `CODE_ROOT`, then a walk up to the code-root `AGENTS.md` plus `personal/`, then `~/code`.
- `personal_dir() -> Path`
- `org_dirs() -> list[Path]`
- `app_data_dir() -> Path` (platformdirs)
- `repo_parent_dirs() -> list[Path]`
- `managed_projects_config() -> Path`

- [ ] Tests: correct from a worktree (the regression), with an env override, on Windows paths (`as_windows`), and with no marker.
- [ ] Replace the five resolvers and the `desktop_manager.runtime.base_dir` uses in `rig/db/*`. This removes 2 of the 4 `rig → desktop_manager` edges.
- [ ] Remove `/Users/ryankelly/code/personal/` from `--all` help strings.

### PR B2: `RepoSlug`
**Files:** Create `src/rig/repo_slug.py`. Modify the parsers in `repo_resolver.py:35,120`, `packages.py:46,174`, `git_facts.py:44`, `estate/orca.py:147`, `estate/prs.py:223`.
- [ ] Table test of remote-URL forms (https, ssh, scp-like, `.git` suffix, enterprise hosts). Replace the five parsers, and drop the `gh repo view` call in `estate/prs.py`.

### PR B3: `rig/proc.py` with untrusted-path hardening
**Files:** Create `src/rig/proc.py` and `tests/test_proc.py`. Modify `src/rig/tracing.py`.

**Interfaces:**
- `Ran(ok, code, out, err)`
- `ProcError`
- `git(*args, cwd, timeout=30, env=None, check=False) -> Ran`
- `git_lines(...) -> list[str] | None`
- `gh(*args, cwd=None, timeout=60, check=False) -> Ran`
- `gh_json[T](kind, *args) -> T | None`
- `GhRunner` Protocol, moved from `github_prs.py`

- [ ] Tests against real temp repos:
  - a hook planted in an `external/` clone does **not** run;
  - the same hook in `personal/` does;
  - `fetch` still works under hardening.
- [ ] Run everything through `tracing.run`, adding `stdin=DEVNULL`, `CREATE_NO_WINDOW` and `encoding="utf-8", errors="replace"`.
- [ ] Hardening under `code_root()/external|other`:
  - `-c core.hooksPath=<devnull>`
  - `-c core.fsmonitor=false`
  - `-c protocol.ext.allow=never`
  - `-c submodule.recurse=false`
  - `GIT_TERMINAL_PROMPT=0`

### PRs B4–B9: Migrate callers, one package per PR
Each PR replaces local `_git`/`_run`/`run_gh` helpers with `rig.proc` and moves the package's tests to a fake `GhRunner` or real temp repos.
- [ ] B4: `rig/estate`: `checks.py:57`, `risk.py:31`, `shelf_store.py:32`, `cleanup.py:74`, `trim.py:122`, `github_archived.py:42`, `prs.py:154`, `locate.py:62`.
- [ ] B5: `rig/workspace`: `package_sources.py:50`, `engine.py:128`, `repo_resolver.py:303`. Also `rig/github_prs.py:57`, and drop its `subprocess.run` patches (`test_github_prs.py:311,376`).
- [ ] B6: `rig/repo_governance`: 35 `subprocess.run(["gh"…])` sites. Dedupe `_gh_api` (`github_apps_manager.py:84` = `release_environments.py:161`), `branch_cleanup.py:105`, `repo_discovery.py:46,98,244`, `agents_md_audit.py:361`.
- [ ] B7 (Windows CI): `desktop_manager`: `work/git_facts.py:59`, `claude_sessions/folders.py:21`, `chrome/cli.py:199`, `projects.py:476,525`, `runtime.py:151`.
- [ ] B8: `repo_health/cli.py:38`, `devenv/steps/git.py:19,25`. Merge `estate/prs.py` and `work/prs.py` onto one branch→PR lookup.
- [ ] B9: `docs/observability.md` is now true (estate spans every git/gh call). Update the code-root `AGENTS.md` hardening claim.

### PR B10: Ban raw subprocess
- [ ] Add Ruff `banned-api` entries for `subprocess.run`, `Popen`, `check_output` and `call`, with a per-file-ignore for `src/rig/proc.py` and a temporary list of the non-git callers that remain. Separate PRs then shrink that list.

## Chain C: Deletions (each PR based on `main`, all in parallel; TODO §3)

Each PR deletes code, tests, docs mentions and config residue together. Order doesn't matter. Each one also
shrinks the import-linter ignore list in G2.

- [ ] C1: `desktop_manager/state_reconcile.py` and its test.
- [ ] C2 (Windows CI): the retired restore executor (`restore.py:162-395`), `workspace_restore.restore_workspace`, the test-only `restore_planner.build_restore_execution_plan` and its tests, and the duplicated helpers in `restore_steps.py:586-753`. This removes 4–6 desktop_manager cycle edges.
- [ ] C3: the ASCII-canvas visualiser (`visualization.py:25-102,514-798`). Move the scoring helpers into `window_reuse.py`.
- [ ] C4: `image_drift/refine_engine.py` and its tests.
- [ ] C5: asset-server ops scope and dead endpoints, the 3D studio, `templates/vendor/` (2.1MB), optical presets and `rig/db/presets_repo.py`. Keep index, static, media, projects, diff, snapshot and mtime.
- [ ] C6: unscored explorers (`rhombille_builder`, the columns mark, `hex_geometry` minus `Point`) moved to `experiments/`, plus their tests. Update `docs/brand-assets/README.md`.
- [ ] C7: vtracer `benchmarks.py` and the self-graded half of `bidirectional.py`. Add an `autotraced` entry to `PATHWAYS`.
- [ ] C8: archive the YouTube subsystem (tag or branch, or move to `hearth`). Drop `typesafe-sdk`.
- [ ] C9: dead obsidian pieces: 14 classes in `models/types.py`, `models/v1_0_0.py`, `markdown_link_checker.py` and fixtures, and the dead `utils.py` helpers.
- [ ] C10: `chrome-tabs install`, `workspace install-startup` / `startup_task.py` and `install-guard`. Keep `write_shim`. Update `docs/desktop-manager/runtime.md`.
- [ ] C11: devenv: `devenv skills` facade and its second registry, `devenv check`, dead config knobs, the unregistered prerequisites step. The engine becomes a list of steps.
- [ ] C12: skills disabled-feature, `repo-governance.sync.json`, the injected AGENTS.md conventions block.
- [ ] C13: `latex_builder`. Add an equivalent `latexmk` script to the blog repo first, as a separate PR there.
- [ ] C14: Tailwind build → `design_system` CSS. Remove `css:build`/`css:check` from CI and docs.
- [ ] C15: agent_browser reduced to `rig browser setup`. Fix the profile writer conflict, and update `docs/agent-browser/`.
- [ ] C16: dependencies: drop `obsidiantools` (keep one wikilink parser) and its stub, dev `pytest-mock`, dev `pytest-bdd` with `tests/features/`, and the duplicate `mdformat`. Declare `click` directly.
- [ ] C17: the production-cuts exporter. Replace both exporters with one CSV-driven exporter, plus a test that cuts derive from `production-cuts.csv`. This is a fix rather than a cut, but it sits here because C4 removes the labs exporter.

## Chain D: Desktop port and restore behaviour suite (after B1 and C2; sequential; Windows CI on every PR)

### PR D1: `DesktopPort`
**Files:** `desktop_manager/queries/`, `commands/`, `handlers/restore_handler.py`, `handlers/restore_steps.py`

**Interfaces:**
- `DesktopPort` Protocol: Queries and Commands merged, plus `clock`/`sleep`, modelled on `align.AlignPorts`.
- `Win32DesktopPort`.

- [ ] Route the leaks through the port: `restore_steps.py:212,288,632,656`, the handler's `time.sleep`, `_settle_pending_app_updates` and `start_terminal_command_upgrades_for_plan`.
- [ ] Fix the fresh-`WorkspaceQueries()` bug (`restore_steps.py:266`), with a test.

### PR D2: Pure geometry layer
- [ ] Move `placement_rect_for`, `rect_similarity` and `infer_placement_for_window` into `desktop_manager/geometry.py`, so `win32/` imports the domain layer, not features.
- [ ] Move the reconciliation orchestration out of `win32/reconcile.py`.
- [ ] Build one reconciler that both align and restore use. Delete the never-called `WorkspaceCommands.reconcile_virtual_desktops`.

### PR D3: Typed `DesktopDiff`
- [ ] Return a structured diff (matched, misplaced, missing, extra, borrowed per screen), with `render()` on top. Replace the substring checks at `restore_steps.py:557`, `cli.py:146,417,517` and `cli_common.py:60-103`.
- [ ] Regression test: a window titled `"extra: missing:"` doesn't change the outcome.

### PR D4: One window matcher
- [ ] Unify `window_reuse.claim_reusable_window`, the visualisation matcher and align's cross-desktop pass, with policy knobs. Table tests from the incident cases.

### PR D5: `FakeDesktop` and restore/align behaviour suite (see chain F)
- [ ] `tests/fakes/desktop.py`: a stateful fake typed as `DesktopPort`.
- [ ] Step library `tests/behaviour/steps/desktop.py`.
- [ ] Dry-run plan scenarios: reuse, launch, satisfied, orphan, close, retire.
- [ ] Execution scenarios: retries, second pass, reuse-fails-then-launch, sibling reservation, invoking-window preservation, return-to-start.
- [ ] Delete the replaced tests (TODO §4: about 2–2.5k lines and about 350 patches).

### PR D6: Remove the facade and compat shim
- [ ] Delete `RestoreCompatibilityCommands` and `workspace_restore.py`; the CLI calls the handler.
- [ ] Have the CLI import the real modules, then delete `windows_workspace.py`.
- [ ] Split `test_workspace_win32_behavior.py` into per-adapter tests.

### PR D7: Chrome and Windows Terminal ports
- [ ] `ChromeDriver` and `Presence` Protocols injected via `ctx.obj`, replacing the six inline `ChromeUia()` sites. Add `FakeChrome` and `TerminalDriver`.
- [ ] Fold the organise tests into one behaviour file. Drop `organise.__wrapped__`.
- [ ] Move the shared Win32 helpers from `chrome/input_windows.py` into `win32/`, and rebase `wt_uia.py` on them.
- [ ] Add `win32/tasks.py` (`TaskSpec`), replacing four hand-built XML writers.

## Chain E: Governance plan (after B6; sequential)

### PR E1: `Resource` protocol and `plan` command
**Interfaces:**
- `Resource[D, O]` with `desired(cfg, repo) -> D`, `read(gh, repo) -> O`, `diff(D, O) -> list[Change]` and `apply(gh, repo, changes)`.
- `repo-governance plan [--apply] [--json]`.

- [ ] Port `release_environments`, `vercel_gating` and `ruleset_drift` first, since they already have this shape. Then port repo settings, labels, Pages and the hygiene files.
- [ ] Make exit codes consistent: 0 clean, 1 drift, 2 error.

### PR E2: Config-first policy
- [ ] Move into `repo_policies.json`: the `gh repo edit` flags, labels, and the `detect_repo_metadata` email and framework strings.
- [ ] Remove `sync-policies --config`.
- [ ] Merge the two `jinja2.Environment` setups.

### PR E3: `FakeGitHub` and plan behaviour suite
- [ ] `tests/fakes/github.py` implements `GhRunner`, with responses keyed by `(method, route)` and a write log.
- [ ] Scenarios:
  - a stale required check is refused;
  - drift produces one change;
  - apply is idempotent.
- [ ] Replace the patched sections of `test_align_repo_conventions.py`, `test_pages_manager.py` and the gh parts of the owner, org and apps tests.

### PR E4: Cut triage
- [ ] Delete `issue-triage`, `pr-triage`, `apply-policy` and `sync-policies`.
- [ ] Reduce `verify-pr-checks` to a roughly 20-line wrapper over `gh pr checks --required --watch` and `mergeStateStatus`, or delete it if no agent instructions reference it.

### PR E5 (optional, later): Pulumi `github` provider
- [ ] Spike only, behind an ADR amendment.

## Chain F: Test infrastructure and gates over tests (F1 on `main`; the rest alongside D and E)

### PR F1: type-ratchet per-path rule sets
**Files:** `packages/type-ratchet/src/type_ratchet/{config,rules,gate}.py`, its README and tests
- [ ] Allow rule sets keyed by path globs, so different rules can apply to `src/` and `tests/`.

### PR F2: Fakes and steps layout
- [ ] Create `tests/fakes/` and `tests/behaviour/steps/`, and document them in `AGENTS.md` (Pytest section).
- [ ] Add the fakes that already exist (`GhRunner` fake, real-git fixture) as the first tenants.

### PR F3: Test-side ratchet rules
- [ ] `fake-location`: `Fake*`/`Stub*` classes only in `tests/fakes/`.
- [ ] `patching`: `mock.patch`, `monkeypatch.setattr` and `sys.modules[...] =` only in allowlisted files.
- [ ] Seed the allowlist with today's files, and shrink it as D, E and the per-area suites land.

### PR F4: Contract suites
- [ ] One parametrised suite per port, run against the fake and the real adapter:
  - git: always, with real temp repos;
  - GitHub: a recorded-response fixture;
  - desktop and chrome: the real adapter on the Windows runner only.

### PRs F5–F8: Per-area behaviour suites (TODO §4)
- [ ] F5 image_drift: tmp-project `CliRunner` suite. Delete `test_dashboard_frontend` and the tests that can't fail.
- [ ] F6 obsidian: remove the global `get_vault_path()` from `run_*`, then add scenario tables through `vault graph *`.
- [ ] F7 estate and orca: `CliRunner --json` over temp repos. Merge the two Orca clients into `rig/orca/` (TODO §2) in the same chain.
- [ ] F8 devenv: steps return `Plan[Change]`; `--dry-run --json` with `HOME=tmp` and inline-snapshot. Replace the doctor tests that hit the real machine.

## Chain G: Architecture gates and surface (last)

### PR G1: import-linter, with an ignore list (can land early, right after chain 0)
- [ ] Add `import-linter` as a dev dependency, with `[tool.importlinter]` in pyproject (start from `temp/evaluation/importlinter.ini`). Contracts:
  - `layers`: `desktop_manager` above `rig`;
  - `acyclic_siblings`: `rig`, `desktop_manager`;
  - `forbidden`: `desktop_manager.win32` must not import feature packages;
  - `independence`: among rig features.
- [ ] List every current violation in `ignore_imports` (from `temp/evaluation/cycles.txt`). Wire it into CI and `uv run status`.

### PR G2: Shrink the ignore list
Run this repeatedly as B, C and D land.
- [ ] Remaining manual cuts, as TODO §5 describes:
  - `estate/apps_windows → win32.dlls`;
  - `rig_cli → desktop_manager.cli`: moves to the composition root, which is allowed explicitly;
  - `claude_sessions.cli → chrome.presence`;
  - `cli_machine → display_guard_windows`;
  - `work.sessions → claude_sessions.*`;
  - `workspace/ci_cli → estate.ci_telemetry`: move the module;
  - `workspace/cli → repo_governance.gitignore_manager`: mount from `rig_cli`.

### PR G3: Empty the ignore list
- [ ] Delete `ignore_imports`. From here every cycle or layer violation fails CI.

### PR G4: Remaining gates
- [ ] deptry with `package_module_name_map` and Windows exclusions.
- [ ] Ratchet path-owned primitive rules (`Path(__file__).parents`, `LOCALAPPDATA`, `/Users/`).
- [ ] Ruff `banned-api` for `ctypes`/`comtypes` outside `win32/`.
- [ ] Close the B10 subprocess allowlist.

### PR G5: One CLI root
- [ ] Mount `vault`, `image-reconcile` (as `assets`), `repo-governance` (as `governance`), `desktop-manager` (as `desktop`) and `agent-browser` (as `browser`) under `rig`. Keep `help`, `status` and `agent` top-level.
- [ ] Rename the verbs that collide between `estate` and `workspace`. Drop the `artifacts`, `name-desktops`/`ensure-desktops`/`sync-desktops` and `rig sync`/`rig doctor` aliases.
- [ ] Update ADR 0017.

### PR G6: Documentation drift sweep
- [ ] Work through TODO §7 (ADRs 0007, 0008, 0017 and 0018, the README, `runtime.md`, `vault-lint-policy.md`, the dead link). Bring AGENTS.md back under its own 120-line audit limit by moving detail into the docs.

## Dependency graph

```
0.1 ─┬─ A1..A8 (parallel; A8 after A4)
     ├─ C1..C17 (parallel)
     ├─ G1 ─────────────────────────────── G2 (repeat) ── G3 ── G4
     ├─ F1 ── F2 ── F3 ── F4
     └─ B1 ── B2 ── B3 ── B4..B9 ── B10
                 │          └─ E1 ── E2 ── E3 ── E4
                 └─ (with C2) D1 ── D2 ── D3 ── D4 ── D5 ── D6 ── D7
F5..F8 follow the seam they need (F7 after B4, F8 after C11).  G5, G6 last.
```

## Done when

- `lint-imports` passes with no `ignore_imports`.
- The only `subprocess` callers are `rig/proc.py` and allowlisted non-git tools.
- No test file outside the allowlist patches modules.
- Every port has exactly one fake, plus a contract suite.
- TODO §1 is empty.
- `docs/target-architecture.md` describes the code as it is.
