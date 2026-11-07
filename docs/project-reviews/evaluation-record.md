# Run evaluation record template

Copy into an operator-owned evaluation comment. Replace placeholders and retain
UNKNOWNs. Use the [rubric](evaluations.md) at a recorded repository commit.

```text
<!-- rig-review-eval:<schedule-key>:<original-run-id>:v1 -->
Evaluation date / evaluator / human adjudicator:
Schedule key / original occurrence ID / attempts and retries:
Task response / report comment / marker:
Run policy commit / paths:
Evaluator rubric commit / rubric version: 1
Evidence packet / before-after snapshots / trace availability:
Model/judge and settings if known:
Operational outcome: DELIVERED | BLOCKED | FAILED | PARTIAL PUBLICATION | UNKNOWN
Planned scope / explicitly unassessed scope:

| Criterion | PASS / FAIL / UNKNOWN / NOT APPLICABLE | Evidence / reason |
| --- | --- | --- |
| G1 Authority | | |
| G2 Boundaries | | |
| G3 Truth | | |
| G4 Continuation/publication | | |
| Q1 Selection | | |
| Q2 Repetition | | |
| Q3 Coverage/fairness | | |
| Q4 Usefulness/efficiency | | |

Evaluation verdict: PASS | FAIL | INCONCLUSIVE
Claims checked / unsupported / unknown:
Deep investigations checked / unjustified repeats:
Due items examined / advanced / deferred / oldest eligible age:
Publication markers/readbacks checked:
Duration / usage: observed value + source, or UNKNOWN
Sampling / missing evidence / disagreement / limits:
At most one proposed refinement ID / hypothesis / acceptance criterion / case IDs:
Next evaluation due / owner:
```

For index #375, keep a compact operator-maintained register:

| Refinement ID | Evidence / baseline | Hypothesis / acceptance | Policy paths / PR | Cases / comparison | State / owner | Canary run / next check / rollback |
| --- | --- | --- | --- | --- | --- | --- |

Record batch counts and links separately from complete-report pointers. An eval
comment is neither a source assessment nor a new scheduled worker report.
