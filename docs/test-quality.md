# Measuring test quality

This guide covers what the research says about measuring test quality objectively, what rig
measures today, and the scorecard rig is building for each type of test. For how to write
tests and the gates they must pass, read [testing.md](testing.md).

## The short version

- **Coverage is a floor, not a quality measure.** It shows which code ran. It says nothing
  about whether anything checked the result.
- **Mutation score is the best evidence of quality.** Use it to decide which tests to improve.
- **Judge each type of test differently.** Unit, integration, property and BDD tests each
  earn their place in a different way, so the same number can mean different things.
- **A test that covers no line uniquely may still be needed.** Before deleting it, check
  whether it kills any mutant that no other test kills.

## What the evidence says

Citations marked † were checked against the paper or an abstract while this guide was written.
The others are well-known results, but check them before quoting.

| Measure | Strength of evidence | Findings |
|:--|:--|:--|
| Line and branch coverage | Moderate as a floor, weak as a measure of quality | Inozemtseva & Holmes (ICSE 2014): once suite size is accounted for, coverage is only low-to-moderately correlated with how many mutants a suite catches. Gopinath, Jensen & Groce (ICSE 2014): statement coverage was the best coverage predictor of mutation score across hundreds of projects. Kochhar, Thung & Lo (SANER 2015)†: the correlation with real bugs ranged from moderate to strong. |
| Mutation score | Moderate to strong as a guide | Just et al. (FSE 2014): 73% of real faults match common mutants. Detecting mutants predicts detecting real faults, independently of coverage. Papadakis et al. (ICSE 2018)†: the correlation is weak once suite size is controlled, but suites with high mutation scores still beat random suites of the same size. Chen et al. (ASE 2020)†: treat suite size as a covariate, not a confounder. Petrović et al. (Google, ICSE 2021): showing surviving mutants in code review leads to better tests. |
| Checked coverage | Moderate, mostly one group's work | Schuler & Zeller (ICST 2011, STVR 2013)†: count only the statements whose result reaches an assertion. This tracks oracle quality more closely than plain coverage. Zhang & Mesbah (FSE 2015): the number of assertions correlates strongly with effectiveness, which makes it a cheap stand-in. |
| Pseudo-tested functions | Moderate | Niedermayr, Juergens & Wagner (2016). Vera-Pérez et al. (EMSE 2019)† replicated it on 21 projects. If a function's body can be replaced and its tests still pass, the tests run it but never check it. This is roughly ten times cheaper to find than full mutation testing. |
| Test smells | Weak to moderate | PyNose (Wang et al., ASE 2021)† and pytest-smell†. Smells predict maintenance cost and flakiness more than missed bugs. |
| Flakiness | Strong as a cost signal | Gruber et al. (ICST 2021)†: across PyPI projects, 59% of flaky tests depended on test order. |
| Redundancy (unique coverage) | Mixed | Two studies disagree on whether you lose fault detection by dropping tests that add no coverage. Wong et al. (1995) found little loss. Rothermel et al. (1998, 2002) found a lot. Yoo & Harman (STVR 2012) survey the field. |

## Classifying tests

A test has two independent properties: its **style**, which is how it is written, and its
**size**, which is the resources it actually uses at runtime.

- **Style** is example, property (Hypothesis) or bdd (pytest-bdd). `tests/conftest.py` tags
  property and bdd tests automatically. Everything else is an example test.
- **Size** follows Google's definitions, as `rig.repo_governance.size_probe` applies them:
  - **small**: one process, with no subprocess, filesystem write or socket;
  - **medium**: subprocesses, filesystem writes or localhost sockets;
  - **large**: a socket to another machine, or the `integration` marker (a live desktop).

Classify by the size a test actually uses, not by what its author intended.
Trautsch & Grabowski (ICST 2017)† found that Python developers overestimate how many unit
tests they write. Trautsch, Herbold & Grabowski (JSS 2020)†, studying 38,782 tests, found
that unit and integration tests catch the same kinds of defect. The difference is that unit
tests locate the defect more precisely.

The existing `integration` marker is narrower than size. It means the test needs a live
desktop session.

## A scorecard for each type

| Type | Measure it by | Ignore |
|:--|:--|:--|
| Small (unit) | Mutants killed in its own module; pseudo-tested functions it reaches; assertions per test; runtime | Unique lines: its job is to locate a fault, not to reach new code |
| Medium / large (integration) | Lines that no small test reaches; mutants that only it kills; flakiness under random order and reruns; runtime | Assertion density |
| Property | Mutants killed per test; how evenly its inputs spread (`event()` and `target()`); share of inputs discarded | Unique lines: it runs code that other tests run, but over many more inputs |
| BDD | Scenarios with identical execution traces; step reuse; mutants killed in behaviour-level code | Line counts |

Ravi & Coblenz (OOPSLA 2025)† found that, per test, a property test found about 50 times as
many mutants as an average unit test. A preprint reports the totals the other way round. Both
can be true, so compare tests one at a time, not suite against suite.

For BDD, Binamungu, Embury & Konstantinou (2018–2020)† find duplicate scenarios by comparing
their execution traces. rig's per-test coverage data gives each scenario exactly that trace.

## What rig measures today

| Measure | Tool | Where |
|:--|:--|:--|
| Line and branch coverage, ratcheting towards 95% | pytest-cov with `rig.repo_governance.coverage_floor` | Every CI run ([testing.md](testing.md)) |
| Lines covered and unique lines for each test | `rig.repo_governance.coverage_by_test` | Every CI run: a job summary plus the `coverage-by-test` artifact |
| Style and size of each test | `pytest --record-sizes PATH`, joined by `coverage_by_test --sizes PATH` | Every CI run: a table by style and size in the job summary, and `style`/`size` columns in the CSVs |
| Mutation score | `uv run --with mutmut mutmut run` | On demand |

The first measurement, on 2026-11-05, counted each test function as one test:

- 2,677 test functions run 22,155 lines. Only 26% of those lines are run by exactly one test.
- 63% of test functions run no line that another test does not also run.
- **190 groups of tests (612 tests in all) run exactly the same lines.** Most are example
  tables written as separate functions over one helper. Turn each group into a parametrized
  test rather than deleting it. The exception is `test_web_spa`: those tests drive a browser,
  and the code they test is JavaScript, which coverage cannot see.
- **81 tests each run 300 or more lines, none of them unique.** They are the candidates for
  merging into a shared fixture with sharper assertions. Check them against mutants before
  merging.
- **38 source files are reached by no test at all.** That is 863 missed statements, and each
  file needs one smoke test through `CliRunner`.

### The numbers are repeatable

Two runs used to agree on unique counts, but the covered count moved for about 2% of tests.
Under xdist, the test that first reaches a lazy import or a cache depends on which worker it
lands on, and module-level code and cache warm-up were credited to that test. Two fixes removed
this. Definition lines are import-only in the report, and `tests/conftest.py` clears every `src`
cache before each test. Two runs now give byte-identical results at every level.

### Sizes, measured

`--record-sizes` installs an audit hook (PEP 578) and records what each test's body does. Fixture
setup is left out, so a `tmp_path` a test never writes to does not make it medium. On 2026-11-05:

- **About 70% of tests are small and 29% medium.** Most medium tests only write files; 179 also
  start a subprocess, usually `git`.
- **One test reaches the internet.** `test_cli_packages_status_and_artifacts_alias` resolves
  `registry.npmjs.org` on every run. It is large, unhermetic, and next in line to fake.
- **There are 19 property tests and 12 BDD scenarios.** The `bdd` marker had selected nothing:
  pytest-bdd 8 dropped the `__scenario__` attribute that `conftest.py` looked for. A test now
  checks that both derived markers reach the suite's own tests.

## Python tooling

| Measure | Tool |
|:--|:--|
| Mutation, per test | mutmut 3, which maps tests to mutants. It needs `fork`, so it runs under WSL on Windows. cosmic-ray is an alternative. |
| Pseudo-tested functions | No Python tool was found. rig can build one: replace each function body, then rerun only the tests that the coverage data says reach it. |
| Checked coverage | No Python tool was found. A practical stand-in is to count `assert` statements in each test with `ast`. |
| Flakiness | pytest-randomly for order, plus pytest-rerunfailures or pytest-flakefinder |
| Smells | pytest-smell |
| Property-test statistics | Hypothesis observability (`HYPOTHESIS_EXPERIMENTAL_OBSERVABILITY`)† |

## Build order

1. **Make the per-test numbers repeatable.** Done: definition lines are import-only, and caches
   are cleared between tests.
2. **Tag each test's size at runtime.** Done: `--record-sizes` writes a sidecar file and
   `coverage_by_test --sizes` groups by it. Pytest's `-m` cannot select on size, because it is
   only known after the test runs.
3. **Build a detector for pseudo-tested functions.** Use the coverage data to choose which
   tests to rerun.
4. **Map each test to the mutants it kills.** Run mutmut 3 as a scheduled CI job, not on
   laptops. This mapping decides which tests with no unique lines can really go.

## Where the evidence disagrees

1. Whether coverage predicts effectiveness depends on whether suite size is controlled.
   Compare Inozemtseva & Holmes with Gopinath et al. and Kochhar et al.
2. Just et al. are positive about mutation score as a proxy, and Papadakis et al. are
   cautious. Both rank it above coverage as a guide.
3. Studies disagree on how much fault detection you lose by minimising a suite while keeping
   its coverage. Hence the rule to check mutants before deleting anything.
4. Most of the evidence for checked coverage comes from its authors. The link from test
   smells to defects is weak.
