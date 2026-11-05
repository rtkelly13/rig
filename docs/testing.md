# Testing

Which style of test to write, and how to measure whether a test is any good.
[development.md](development.md) has the markers, the Windows-safe rules and the CI gates.

## Three styles, chosen by what is being pinned

| Style | Pins | Write one when | Lives in |
|:------|:-----|:---------------|:---------|
| **BDD** (pytest-bdd) | A rule a person would state: what the tool promises | Losing the behaviour costs real work or trust, and the rule is a decision table someone should be able to read | `tests/features/*.feature` and a `test_*_bdd.py` of steps |
| **Property** (Hypothesis) | An invariant of an algorithm, for every input | The code is pure and the rule holds across a domain: tiling, round-trips, idempotence, symmetry, agreement with an oracle | `test_*_properties.py` beside the example tests |
| **Example** | One concrete case | A specific regression, an edge the generator won't find, or mechanics below the level of a rule | `test_*.py` |

Both `property` and `bdd` markers are applied automatically in `tests/conftest.py`, never by hand.
Use `uv run pytest -m property` or `-m bdd` to run one style. Everything left is example-based.

### BDD: model the rules, not the helpers

A feature file is the readable contract. Group scenarios under `Rule:` and put decision tables in
a `Scenario Outline`, so the rows *are* the specification. `features/estate_shelve.feature`
is the model to follow:

- **Steps drive real collaborators.** That means real git against a bare origin, not mocks of
  `subprocess`. A step that only checks a mock was called states nothing a reader cares about.
- **One `Then` is one observable fact, asserted exactly.** Use `==` on the full status list, not
  `in` on a substring.
- **When a feature replaces example tests, delete them.** Then prove nothing was lost (see
  [Coverage parity](#coverage-parity-for-test-refactors)).

Good candidates are the behaviours whose failure loses work or leaks something: shelving, tidy,
branch cleanup, workspace restore with window reuse, and governance sync (what it changes and what
it never touches).

### Properties: state the rule, then let Hypothesis find the counterexample

Useful shapes, each with an example in `tests/desktop_manager/test_placement_properties.py`:

- **Partition.** The slots of a layout cover their parent exactly once.
- **Agreement.** The pixel and unit layouts agree whenever the screen divides evenly.
- **Oracle.** The clever implementation matches a brute-force one on a small grid.
- **Algebra.** Symmetry, identity, invariance under translation or reordering, monotonicity.
- **Round-trip and idempotence.** `parse(render(x)) == x`, and `align(align(x)) == align(x)`.

A defect a property finds but nobody has fixed yet becomes an `xfail(strict=True)` example.
When someone fixes it the test turns red, which prompts promoting it into the property.

Profiles live in `tests/conftest.py`. `dev` explores new examples and keeps failures in
`temp/hypothesis/`. `ci` (selected when `CI` is set) is derandomized, so a CI run is
reproducible: replay one locally with `HYPOTHESIS_PROFILE=ci`.

## Measuring test quality

Each measure answers a different question. None of them is a target on its own.
[test-quality.md](test-quality.md) has the evidence behind each measure and how each type of test is scored.

| Measure | Answers | Command |
|:--------|:--------|:--------|
| Line + branch coverage | What code runs at all? | `uv run pytest --cov=src` (`branch = true` is on; the floor is a ratchet, below) |
| Per-test footprint | How many lines does each test execute, and how many does no other test? | `uv run pytest --cov=src --cov-context=test`, then `uv run python -m rig.repo_governance.coverage_by_test` |
| Style and size | Is each test example, property or bdd, and small, medium or large by what it actually ran? | Add `--record-sizes temp/sizes.json` to that pytest run and `--sizes temp/sizes.json` to the report ([test-quality.md](test-quality.md#classifying-tests)) |
| **Mutation score** | **Would the tests notice if this code were wrong?** | `uv run --with mutmut mutmut run`, then `uv run --with mutmut mutmut results` |

Coverage says code was *executed*. Mutation testing says it was *checked*. In the 2026-11-04
baseline, `gitignore_manager.py` had 96% line coverage and a 61% mutation score. Its tests ran
nearly every line and asserted on little of it.

### Per-test footprint: covered and unique lines

`rig.repo_governance.coverage_by_test` reads a `.coverage` file recorded with
`--cov-context=test`. For every test it reports two numbers:

- **covered**: the distinct source lines the test executes, counting setup, call and teardown;
- **unique**: the lines among those that no other test executes. If the test is deleted, the suite
  stops running exactly those lines.

```
uv run pytest -n auto --cov=src --cov-context=test --cov-report=
uv run python -m rig.repo_governance.coverage_by_test --group function --csv temp/by-test.csv
```

`--group` sets the unit that counts as one test: `item` (each parametrized case), `function`
(all cases together) or `file`. Uniqueness is judged against other units at the same level.
Lines that run only while modules are imported for collection count as `import_only` and are
credited to no test. CI writes the function-level summary to the job summary and uploads all
three levels as the `coverage-by-test` artifact. Recording contexts adds no measurable time.

How to read the numbers:

- **Zero unique, large covered:** the test reaches only code that other tests already reach. It
  is a candidate to fold into a parametrized sibling or delete. Check its assertions before you
  delete it, because coverage counts execution, not checking.
- **Zero unique, small covered, on pure code:** this is expected for property tests and
  pure-helper tests. They target a function that a larger test also executes, but they check far
  more inputs. Mutation score is the right judge for these, not uniqueness.
- **Many unique lines:** this test is the only one guarding those lines, so its assertions are
  the only defence. Look there first when you sharpen assertions.
- **High covered, few unique, many such tests in one file:** each test pays for the same setup
  repeatedly. One shared fixture plus focused assertions usually does the same job for less.

### The coverage floor is a ratchet towards 95%

`[tool.coverage.report] fail_under` in `pyproject.toml` is the floor. pytest-cov fails any
`--cov` run below it. In CI, `rig.repo_governance.coverage_floor` adds two rules:

- **It never falls.** A pull request whose floor is below its base branch's floor fails.
- **It follows coverage up.** Once coverage is more than `slack` (2) points above the floor,
  the check fails and names the new floor to set. It stops at `target` (95), the hard limit
  the ratchet works towards.

So a pull request that adds tests usually raises the floor in the same change, and no later
change can give that coverage back. Win32 and UI Automation adapters are left out of the
measurement (`[tool.coverage.run] omit`): the Linux job cannot run them. Keep logic out of
them. Code that can be tested belongs in a module that stays measured.

On 2026-11-04 the floor started at 76 against a measured 76.9%. The first wave (the `estate`
CLIs) took it to 77, a point under the 78.0% measured locally, because CI's measurement can
differ from a local one. Reaching 95 means covering about 8,000 of the 10,400 branch-and-line
units still missed. The remaining waves go by area, largest gap first, and each raises the floor
in the same pull request:

| Wave | Area | Missed units | Floor after, roughly |
| --- | --- | ---: | ---: |
| 1 (done) | `rig/estate` CLIs: `cli.py`, `shelve_cli.py` | 520 | 77 |
| 2 | `rig/repo_governance` CLIs: `cli_sync`, `cli_audits`, `cli_triage`, `cli_repo_checks` | 1,660 | 81 |
| 3 | `rig/image_drift`: `cli_gallery`, `cli_analysis`, `server`, `cli_explore` | 1,220 | 84 |
| 4 | `rig/estate` remainder, `rig/devenv/steps`, `rig/workspace` | 2,230 | 88 |
| 5 | `rig/otel_stack`, `rig/skills`, `rig/obsidian`; `desktop_manager` `work`, `jobs`, `handlers`, `claude_sessions` | 2,000 | 92 |
| 6 | `desktop_manager/chrome` and `win32` | 1,310 | 95 |

Wave 6 is the hard one. Much of it is macOS Accessibility and UI-scripting drivers
(`chrome/drivers/macos_ax.py`, `macos_ui.py`), which the Linux job cannot drive any more than
the Win32 adapters. Each driver either gets its logic moved behind a fake-able seam and tested,
or joins the omit list under the same rule as the Win32 adapters. Decide driver by driver.
Moving the target is not one of the options.

### Mutation testing

`[tool.mutmut]` in `pyproject.toml` scopes it to pure modules with fast tests. mutmut is not a dev
dependency, because it pins click to a release that breaks typer's types. `--with` adds it for one run. To add a module,
put it in `source_paths` and its tests in `pytest_add_cli_args_test_selection`. mutmut works in
`mutants/` at the repo root, which is gitignored.

Read survivors with `uv run --with mutmut mutmut show <name>`. Each one is either:

- **a missing assertion.** Add a property or a sharper `==`. For example, `/` surviving in place
  of `//` meant no test checked that pixel rects are integers.
- **equivalent.** The change cannot be observed, like `zip(strict=False)` → `strict=None`.
  Leave it.

Judge a test change by the mutation score it moves, not by the test count or line coverage it
adds. Adding 15 property tests to `placement.py` moved the score over reachable mutants from
78.5% to 86.6% and barely changed line coverage.

In CI, the `Mutation` workflow (`workflow_dispatch` only) scores each module in the job summary
and uploads every survivor's diff, so tests can be written from the artifact alone. Run it with
`gh workflow run mutation.yml --ref <branch>`. A test with zero unique lines in the per-test
footprints can still be the only one that kills a mutant: `tests/test_gitignore_manager_mutants.py`
added 0.01 points of coverage and killed 88 mutants. Run mutation before deleting such a test.

The `Fuzz` workflow, also dispatch-only, runs the estate state machine
(`tests/test_estate_sync_state_machine.py`) under HypoFuzz and replays what it saved.

### Coverage parity for test refactors

When tests are rewritten (examples folded into a feature, or a file split), compare the exact sets
of covered lines and branches for the module under test, before and after:

```bash
uv run pytest <old tests> --cov=<module> --cov-branch --cov-report=json:temp/before.json
uv run pytest <new tests> --cov=<module> --cov-branch --cov-report=json:temp/after.json
```

The `executed_lines` and `executed_branches` of each file must match, or the difference must be
explained. Totals hide swaps; compare the sets.

## CI time budget

CI time is capped at three levels. Something slow fails loudly instead of growing quietly.

| Cap | Where | Value |
|:----|:------|:------|
| Job | `timeout-minutes` in `.github/workflows/ci.yml` | test 12, lint 10, Windows 15 |
| Test | `timeout` in `[tool.pytest.ini_options]` (pytest-timeout) | 300s; a hang fails that test with a stack trace (on Windows the thread method ends the whole run) |
| Property | `max_examples` in the `ci` Hypothesis profile | 100 per test (Hypothesis's default, made explicit); derandomized, so every run costs the same |

The Linux job runs `pytest -n auto --durations=15`, so every run lists its slowest tests.
On 2026-11-04 the suite took about 4 minutes serially and about 3 minutes with `-n auto` locally.
The slowest test, `test_full_bidirectional_verification`, took 54s under xdist contention.
The cap is for hangs. `--durations` is the signal for slowness: a test that creeps towards a
minute on CI belongs behind `-m integration`, or needs a cheaper fixture.

Mutation testing never runs in PR CI. A mutmut run takes minutes per module, so it is a local
measure, or a scheduled job if one is added later.
Deeper Hypothesis runs are also local: `HYPOTHESIS_PROFILE=dev` explores fresh examples on each
run and saves any failures to `temp/hypothesis/`.
