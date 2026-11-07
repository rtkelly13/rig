# Development

The full command set, test conventions and quality gates for this repo. `AGENTS.md` keeps only
the commands an agent runs every session.

## Commands

`AGENTS.md` lists the everyday ones. Beyond those:

```bash
uv run status --ci                                      # CI-safe tests only (@pytest.mark.ci)
uv run pytest -m ci                                     # the same subset, directly
uv run pytest --cov=src --cov-report=term-missing       # coverage
pnpm install && pnpm css:build                          # rebuild the gallery CSS
pnpm css:check                                          # fails if the committed CSS is stale
```

The gallery CSS (Tailwind 4, CSS-first, so no `tailwind.config.js`) is compiled to
`src/rig/image_drift/templates/gallery.css` and committed, because the Python CLI inlines it and
must work without Node. Rebuild it after touching the templates or `styles/gallery.src.css`.

## Pytest Markers

Mark tests so the CI-safe subset can be run without external dependencies:

- `@pytest.mark.ci` — no Windows APIs, no live system state; runs in any CI environment
- `@pytest.mark.windows` — requires Windows OS with pyvda/win32 APIs
- `@pytest.mark.integration` — requires live state (open windows, virtual desktops running)
- `property` / `bdd` — applied automatically to Hypothesis tests and pytest-bdd scenarios; see [testing.md](testing.md)

CI runs the suite on Ubuntu (`test`, with coverage and Windows portability lint gates). To conserve runner minutes on private repos, `test-windows` on `windows-latest` runs conditionally:
- Nightly on `main` (03:43 UTC), not on every merge — a break that lands on `main` surfaces the next morning. Linux jobs skip the nightly run.
- On pull requests whose branch or title contains `windows` or `win32` (or with label `ci:windows`)
- On manual `workflow_dispatch`

Pure Windows unit tests, path semantics, and mock wrappers execute on Linux CI via `as_windows` test doubles; only tests actually requiring live win32 APIs or native Windows subsystem behavior are deferred to `test-windows`. Anything that drives a real desktop, window or UI Automation tree must carry `integration`, or it will run on the Windows runner.

Test isolation guards (autouse, so new tests get them for free):

- `tests/conftest.py` points `LOCALAPPDATA` and `APPDATA` at per-test temp dirs and clears `DESKTOP_MANAGER_LOG_DIR`, so no test writes into the live runtime, restore logs or plans.
- `tests/desktop_manager/integration/conftest.py` fakes desktop navigation, reports every process as not running, and fails a test that reaches the real `move_existing_window_to_entry`. A test exercising window reuse patches that move itself.

Windows-safe tests (the `test-windows` job catches these, but write them right first time):

- **Links**: create directory links with `tests/links.py` `dir_link`. It makes a junction, which needs no privilege. Use `file_symlink_or_skip` when a test truly needs a file symlink. Never call `os.symlink` directly: without Developer Mode it fails with WinError 1314. Product code goes through `rig.fs_links` for the same reason.
- **Encoding**: always pass `encoding="utf-8"` to `read_text`, `write_text` and `open`. The Windows default is cp1252.
- **Paths in output**: render paths with `.as_posix()` wherever a report, snapshot or assertion compares text. Otherwise Windows prints backslashes.
- **Line endings**: when a test hashes a checked-in file, give that file type `-text` in `.gitattributes`, as `*.svg` has. Otherwise CRLF conversion on checkout changes the digest.
- **Machine state**: never depend on the real monitor count, open windows or running processes. `tests/desktop_manager/conftest.py` fakes three monitors, and the integration conftest reports every process as not running.
- **Filenames**: Windows cannot create names containing `:`, `?` or `*`. Mark those tests with `skipif(sys.platform == "win32")`, as `needs_colon_filenames` does in `tests/obsidian/test_lint_manager.py`.
- **Console width**: compare wrapped CLI output with whitespace normalised. The Windows console wraps at 80 columns.

## Standards and quality gates

- **Package Manager**: `uv` for Python, `pnpm` for the Tailwind build
- **Local Relational & Analytics Database**: DuckDB (`duckdb`). Always prefer DuckDB for local analytics, structured ledger querying, and cross-referencing high-volume JSON/metadata feeds to prevent repetitive API calls.
- **Lint/Format**: Ruff (strict rules), basedpyright (strict type checking, explicit and inferred `Any` banned), ty, Bandit (security)
- **Workflow**:
  - All changes **MUST** be made via Pull Request (no direct pushes to `main`).
  - All CI checks (Lint, Typecheck, Tests) **MUST** pass before merge.
  - Merges **MUST** use Squash only (strictly enforced in repo settings).
- **Quality tools**:
  - **Ruff**: Strict styling, complexity, and logic rules.
  - **basedpyright**: `typeCheckingMode = "strict"` applies to every file; there is no per-path `strict` list. On top of strict, these rules are errors: `reportExplicitAny`, `reportAny`, `reportIgnoreCommentWithoutRule`, `reportImplicitOverride` (mark overrides with `@override`), `reportUnannotatedClassAttribute`, `reportImportCycles` (lazy imports count too; break cycles by moving shared code into a leaf module), `reportUninitializedInstanceVariable`, `reportUnusedParameter` and `reportPrivateLocalImportUsage`. There is no baseline, so every error fails CI.
  - **ty**: `uv run ty check src packages/type-ratchet/src` also gates CI and `uv run status`. It checks with `python-platform = "all"`, so Windows-only code is checked the same way on a Linux runner as on a Windows machine. Untyped Windows modules are listed in `[tool.ty.analysis] replace-imports-with-any`.
  - **Type-soup ratchet**: `uv run ratchet` fails on any fixed tuple, `cast()`, ignore comment, `noqa`, `getattr`, long module, file-level `# pyright:` pragma or string-key read (`x["k"]`, `x.get("k")`, apart from `os.environ` and the `str-key-receivers` in pyproject) under the `paths` in `[tool.repo-health.ratchet]` (`src/` and the ratchet's own package), on any `json`/`yaml` parse or `isinstance(x, dict)` outside the `typed_json`/`typed_yaml` boundary modules (stub untyped libraries in `typings/` instead), and on any `dict[str, object]`, `Mapping[str, object]` or `JsonObject` (`loose-dict`) outside the serializers and config round-trips in `loose-dict-allow`. Follow [python-types.md](python-types.md) for the banned shapes and their replacements. The ratchet is the standalone, stdlib-only `type-ratchet` package in `packages/type-ratchet` (a uv workspace member); its [README](../packages/type-ratchet/README.md) covers the rules, config keys and git install for other repos.
  - **import-linter**: `uv run lint-imports` checks the `[tool.importlinter]` contracts in pyproject (gates CI and `uv run status`): `desktop_manager` sits above `rig`, sibling subpackages are acyclic, `desktop_manager.win32` does not import features, and the main `rig` features are independent. Each contract's `ignore_imports` lists today's violations with the plan item that removes it. Don't add to it: fix the import or move shared code into a leaf module. An entry that no longer matches fails the run, so delete it in the PR that removes the import.
  - **Bandit**: Automated security scanning.
  - **Pytest**: Coverage-enforced testing.
