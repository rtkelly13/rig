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

- **Style** is example or property (CsCheck), or a Gherkin scenario (Reqnroll).
- **Size** follows Google's definitions:
  - **small**: one process, with no subprocess, filesystem write or socket;
  - **medium**: subprocesses, filesystem writes or localhost sockets;
  - **large**: a socket to another machine, or a live desktop (the Windows-only tests).

Classify by the size a test actually uses, not by what its author intended.
Trautsch & Grabowski (ICST 2017)† found that developers overestimate how many unit
tests they write. Trautsch, Herbold & Grabowski (JSS 2020)†, studying 38,782 tests, found
that unit and integration tests catch the same kinds of defect. The difference is that unit
tests locate the defect more precisely.

## A scorecard for each type

| Type | Measure it by | Ignore |
|:--|:--|:--|
| Small (unit) | Mutants killed in its own module; pseudo-tested functions it reaches; assertions per test; runtime | Unique lines: its job is to locate a fault, not to reach new code |
| Medium / large (integration) | Lines that no small test reaches; mutants that only it kills; flakiness under random order and reruns; runtime | Assertion density |
| Property | Mutants killed per test; how evenly its inputs spread; share of inputs discarded | Unique lines: it runs code that other tests run, but over many more inputs |
| BDD | Scenarios with identical execution traces; step reuse; mutants killed in behaviour-level code | Line counts |

Ravi & Coblenz (OOPSLA 2025)† found that, per test, a property test found about 50 times as
many mutants as an average unit test. A preprint reports the totals the other way round. Both
can be true, so compare tests one at a time, not suite against suite.

For BDD, Binamungu, Embury & Konstantinou (2018–2020)† find duplicate scenarios by comparing
their execution traces.

## What rig measures today

| Measure | Tool | Where |
|:--|:--|:--|
| Line and branch coverage, floor 95% | `scripts/coverage-floor.cs` over coverlet reports | Every CI run ([testing.md](testing.md#the-c-coverage-floor)) |

The per-test footprint, size probe and mutation tooling were Python-only (pytest-cov contexts, an audit hook,
mutmut) and went with the Python suite in #285. The 2026-11 baseline they produced is in git history. A C# mutation
tool is the open measurement: it would decide which tests with no unique lines can really go.

## Where the evidence disagrees

1. Whether coverage predicts effectiveness depends on whether suite size is controlled.
   Compare Inozemtseva & Holmes with Gopinath et al. and Kochhar et al.
2. Just et al. are positive about mutation score as a proxy, and Papadakis et al. are
   cautious. Both rank it above coverage as a guide.
3. Studies disagree on how much fault detection you lose by minimising a suite while keeping
   its coverage. Hence the rule to check mutants before deleting anything.
4. Most of the evidence for checked coverage comes from its authors. The link from test
   smells to defects is weak.
