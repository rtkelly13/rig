"""Naming rules and drift detection for required CI status check names.

A required status check is a contract between a repo's workflow files and its branch-protection
ruleset, and nothing in GitHub connects the two halves. Two rules are enforced here:

1. **Drift** (an error): every `required_checks` name in `config/repo_policies.json` must be declared by a job in a
   workflow that triggers on `pull_request`.
2. **Shape** (a warning): check names are lowercase `snake_case` of at most two words, and
   preferably a word from `PREFERRED_CHECK_NAMES`.

See `docs/repo-governance/required-checks.md` for why, and for the incidents that prompted both.
"""

import difflib
import pathlib
import re
from dataclasses import dataclass

import yaml

from rig.repo_governance.workflow_yaml import KeyText, Triggers, workflow_keys
from rig.typed_json import Lenient, try_decode
from rig.typed_yaml import load_yaml

# Lowercase, snake_case or kebab-case, at most two words. One optional separator,
# so `pr_title` and `pr-checks` pass, and `build_and_test_everything` does not.
CHECK_NAME_RE = re.compile(r"^[a-z][a-z0-9]*(?:[_-][a-z0-9]+)?$")

MAX_CHECK_NAME_WORDS = 2

# The shared lexicon: one canonical name per common developer task, so the same
# job answers to the same string in every repo. Add to this rather than
# inventing a synonym -- see docs/repo-governance/required-checks.md.
PREFERRED_CHECK_NAMES: dict[str, str] = {
    # The aggregate gate. Depends on every other job and reports their verdict,
    # so it is the one name a ruleset should ever have to know.
    "ci": "Aggregate gate — passes only if every other job passed",
    "pr-checks": "Pull request validation suite (common aggregate name)",
    "pr_checks": "Pull request validation suite",
    # Build and packaging
    "build": "Compile, bundle or package the artefact",
    "typecheck": "Static type checking (tsc, pyright, mypy)",
    "codegen": "Generated output still matches the source it is generated from",
    # Tests
    "test": "The default test suite (unit tests, when there is only one suite)",
    "tests": "The default test suite (common plural alias for test)",
    "unit": "Unit tests, when split out from other suites",
    "e2e": "End-to-end tests through the real interface",
    "visual": "Visual regression / screenshot comparison",
    "smoke": "Post-deploy sanity check against a running deployment",
    "regression": "Full suite before merge — a commit status, pending until the PR is labelled ready-to-merge",
    "bench": "Performance benchmarks",
    "benchmark": "Performance benchmarks (common alias for bench)",
    "coverage": "Coverage thresholds and reporting",
    # Static analysis
    "lint": "Static analysis and style rules",
    "format": "Formatting check (prettier, ruff format, dotnet format)",
    "a11y": "Accessibility audit",
    "contrast": "Colour contrast of token pairs",
    "tokens": "Design token drift — generated CSS matches its source",
    # Supply chain and policy
    "deps": "Dependency hygiene — unused, unlisted, misplaced, unpinned",
    "security": "Vulnerability, secret and SAST scanning",
    "licenses": "Licence policy compliance",
    "action_shas": "GitHub Actions pinned to 40-character commit SHAs",
    # Docs and metadata
    "docs": "Documentation build or link check",
    "pr_title": "Pull request title convention (Conventional Commits)",
    "pr-title": "Pull request title convention (hyphenated alias)",
    # Release
    "publish": "Publish a package or image to a registry",
    "release": "Cut a release — tag, changelog, artefacts",
    "deploy": "Deploy to an environment",
    "migrate": "Database migration check",
}


def lint_check_name(name: str) -> list[str]:
    """Returns human-readable warnings about a check name's shape. Empty means it conforms."""
    warnings: list[str] = []

    if name in PREFERRED_CHECK_NAMES:
        return warnings

    if name != name.lower():
        warnings.append("not all lowercase")

    # Anything that is not a lowercase alphanumeric or an underscore is a word
    # separator someone chose instead of snake_case: spaces, `&`, `-`, `,`, `(`.
    stray = sorted({c for c in name if not (c.isalnum() or c == "_")})
    if stray:
        rendered = " ".join(f"{c!r}" for c in stray)
        warnings.append(f"not snake_case — separators/punctuation {rendered}")

    words = [w for w in re.split(r"[^a-zA-Z0-9]+", name) if w]
    if len(words) > MAX_CHECK_NAME_WORDS:
        warnings.append(f"{len(words)} words, at most {MAX_CHECK_NAME_WORDS} allowed")

    if not warnings and not CHECK_NAME_RE.match(name):
        warnings.append("does not match lowercase snake_case of at most two words")

    suggestion = suggest_preferred_name(name)
    if suggestion:
        warnings.append(f"not in the preferred lexicon — did you mean {suggestion!r}?")
    elif not warnings:
        warnings.append("not in the preferred lexicon (allowed, but prefer a listed name)")

    return warnings


def suggest_preferred_name(name: str) -> str | None:
    """Maps a non-conforming name onto the closest lexicon entry, or None if nothing is close.

    Matches on the name's words rather than the whole string, because the names this has to
    repair are sentences: `Storybook & Visual Regression` shares no useful prefix with `visual`
    but does contain the word.
    """
    words = [w.lower() for w in re.split(r"[^a-zA-Z0-9]+", name) if w]
    if not words:
        return None

    canonical_snake = "_".join(words)
    if canonical_snake in PREFERRED_CHECK_NAMES:
        return canonical_snake

    canonical_kebab = "-".join(words)
    if canonical_kebab in PREFERRED_CHECK_NAMES:
        return canonical_kebab

    # Where both "tests" and "coverage" appear (e.g. "Tests & Coverage"), prefer "coverage"
    if "coverage" in words and "coverage" in PREFERRED_CHECK_NAMES:
        return "coverage"

    for word in words:
        if word in PREFERRED_CHECK_NAMES:
            return word

    for word in words:
        close = difflib.get_close_matches(word, PREFERRED_CHECK_NAMES, n=1, cutoff=0.8)
        if close:
            return close[0]

    return None


@dataclass(frozen=True, slots=True)
class _Job:
    name: Lenient[str] = None


@dataclass(frozen=True, slots=True)
class _Env:
    # The convention for a check that is a commit status rather than a job: the workflow names the
    # context it posts in a top-level `STATUS_CONTEXT`. A ruleset matches statuses and check runs on
    # the same string, but only a job's name can be read off the YAML, so the status declares its own.
    STATUS_CONTEXT: Lenient[str] = None


@dataclass(frozen=True, slots=True)
class _Workflow:
    """The parts of a workflow file the check-name lint reads."""

    on: Triggers = ()
    jobs: Lenient[dict[KeyText, Lenient[_Job]]] = None
    env: Lenient[_Env] = None


def _load_workflow(path: pathlib.Path) -> _Workflow | None:
    """Parses one workflow file, or returns None if it is unreadable or not a mapping.

    A malformed workflow is skipped rather than fatal: this is a naming guard, and failing the
    whole sweep because one unrelated file does not parse would make it the thing people turn off.
    """
    try:
        loaded = load_yaml(path.read_text(encoding="utf-8"))
    except (yaml.YAMLError, OSError):
        return None
    return try_decode(_Workflow, workflow_keys(loaded))


def discover_pr_check_names(repo_path: pathlib.Path) -> dict[str, str]:
    """Returns {check name: "workflow file :: job id"} for every job that reports on a PR.

    The check name GitHub shows — and that a ruleset matches on — is a job's `name:`, falling
    back to its job id. Only workflows triggered by `pull_request` are considered, because a
    job that never runs on a PR can never satisfy a required check on one.

    A workflow's `env.STATUS_CONTEXT` counts too, as "workflow file :: status": a commit status it
    posts, such as design-system's `regression`, which stays pending until a PR is labelled
    `ready-to-merge`. That gate cannot be a job, because a job skipped by its `if:` reports Success.
    """
    found: dict[str, str] = {}
    workflows_dir = repo_path / ".github" / "workflows"
    if not workflows_dir.is_dir():
        return found

    for path in sorted(workflows_dir.glob("*.y*ml")):
        workflow = _load_workflow(path)
        if workflow is None or workflow.jobs is None or "pull_request" not in workflow.on:
            continue

        for job_id, job in workflow.jobs.items():
            name = job.name if job is not None and job.name is not None else job_id
            found.setdefault(name, f"{path.name} :: {job_id}")

        if workflow.env is not None and workflow.env.STATUS_CONTEXT:
            found.setdefault(workflow.env.STATUS_CONTEXT, f"{path.name} :: status")

    return found
