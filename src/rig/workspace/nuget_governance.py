"""NuGet trusted publishing: what rig can verify around a policy it cannot read.

nuget.org has no API for trusted publishing policies; they are created, listed
and deleted only on the website. A policy matches a GitHub OIDC token on:

- the repository owner and repository, by name and case-insensitively;
- the workflow *file name*, parsed from ``job_workflow_ref``, so for a reusable
  workflow it is the called file;
- the environment, only if the policy sets one.

A new policy is temporary for 7 days. Its first successful publish stores the
repository's owner and repository IDs, and from then on those must match too. So
after a transfer the old policy can never match again: add one for the new
owner, publish, then delete the old one.

This module is the policy-agnostic half: it reads the publish workflow into
facts and renders the checklist of what to enter on the website. ``packages``
turns the facts into findings. It imports nothing from ``packages``, so the
import stays one-way.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import PurePosixPath
from typing import Annotated

import yaml
from pydantic import Field

from rig.repo_governance.workflow_yaml import KeyText, workflow_keys
from rig.typed_json import Lenient, try_decode
from rig.typed_yaml import load_yaml

NUGET_TRUST_URL = "https://www.nuget.org/account/trustedpublishing"
POLICY_LIFETIME_NOTE = (
    "temporary until first publish, 7 days; after a transfer add the new policy, publish, then delete the old one"
)

# `${{ secrets.NUGET_USER || 'rtk_13' }}`: the quoted literal after the last `||` is what runs
# when the secret is unset, which is the value the policy owner has to match.
_FALLBACK = re.compile(r"\|\|\s*['\"]([^'\"]+)['\"]\s*}}\s*$")
_EXPRESSION = re.compile(r"\$\{\{")
_NUGET_SECRET = re.compile(r"secrets\.(NUGET_API_KEY|NUGET_TOKEN|NUGET_KEY\w*)\b")
_PUSH = re.compile(r"\bnuget\s+push\b", re.IGNORECASE)
_SKIP_DUPLICATE = re.compile(r"--skip-duplicate\b|-SkipDuplicate\b", re.IGNORECASE)


# ---------------------------------------------------------------- workflow shape


@dataclass(frozen=True, slots=True)
class _Permissions:
    id_token: Annotated[Lenient[str], Field(alias="id-token")] = None


@dataclass(frozen=True, slots=True)
class _Environment:
    name: Lenient[str] = None


@dataclass(frozen=True, slots=True)
class _LoginInputs:
    user: Lenient[str] = None


@dataclass(frozen=True, slots=True)
class _Step:
    uses: Lenient[str] = None
    run: Lenient[str] = None
    inputs: Annotated[Lenient[_LoginInputs], Field(alias="with")] = None
    env: Lenient[dict[KeyText, Lenient[str]]] = None


@dataclass(frozen=True, slots=True)
class _Job:
    permissions: Lenient[_Permissions | str] = None
    environment: Lenient[_Environment | str] = None
    steps: Lenient[tuple[Lenient[_Step], ...]] = None
    uses: Lenient[str] = None


@dataclass(frozen=True, slots=True)
class _Workflow:
    permissions: Lenient[_Permissions | str] = None
    jobs: Lenient[dict[KeyText, Lenient[_Job]]] = None


# ---------------------------------------------------------------- workflow facts


@dataclass(frozen=True, slots=True)
class LoginJob:
    """The job that runs ``NuGet/login``, read as far as a policy cares."""

    name: str
    id_token_write: bool
    #: The ``user:`` input as written.
    user_input: str | None
    #: The profile name it resolves to: the literal itself, or a ``|| 'literal'`` fallback.
    user: str | None
    environment: str | None
    #: A ``nuget push`` step after the login, in the same job.
    push_found: bool = False
    skip_duplicate: bool = False
    #: ``secrets.NUGET_*`` read by the push step: a long-lived key next to the OIDC one.
    push_secrets: tuple[str, ...] = ()


@dataclass(frozen=True, slots=True)
class PublishWorkflow:
    login_jobs: tuple[LoginJob, ...] = ()
    #: Reusable workflows the file calls; a policy must name the called file, not this one.
    called: tuple[str, ...] = ()


def _id_token_write(permissions: _Permissions | str | None) -> bool | None:
    """``None`` when nothing is declared, so a job can fall back to the workflow's permissions."""
    if permissions is None:
        return None
    if isinstance(permissions, str):
        return permissions == "write-all"
    return permissions.id_token == "write"


def _resolve_user(value: str | None) -> str | None:
    if value is None:
        return None
    fallback = _FALLBACK.search(value)
    if fallback:
        return fallback.group(1)
    return None if _EXPRESSION.search(value) else value.strip()


def _environment_name(environment: _Environment | str | None) -> str | None:
    return environment.name if isinstance(environment, _Environment) else environment


def _login_job(name: str, job: _Job, inherited: bool | None) -> LoginJob | None:
    steps = [step for step in job.steps or () if step is not None]
    login_at = next(
        (i for i, step in enumerate(steps) if (step.uses or "").lower().startswith("nuget/login")),
        None,
    )
    if login_at is None:
        return None
    login = steps[login_at]
    user_input = login.inputs.user if login.inputs else None
    pushes = [step for step in steps[login_at + 1 :] if _PUSH.search(step.run or "")]
    texts = [step.run or "" for step in pushes] + [v or "" for step in pushes for v in (step.env or {}).values()]
    secrets = sorted({m.group(1) for text in texts for m in _NUGET_SECRET.finditer(text)})
    own = _id_token_write(job.permissions)
    return LoginJob(
        name=name,
        id_token_write=bool(inherited if own is None else own),
        user_input=user_input,
        user=_resolve_user(user_input),
        environment=_environment_name(job.environment),
        push_found=bool(pushes),
        skip_duplicate=bool(pushes) and all(_SKIP_DUPLICATE.search(step.run or "") for step in pushes),
        push_secrets=tuple(secrets),
    )


def read_publish_workflow(text: str) -> PublishWorkflow | None:
    """The jobs that log in to nuget.org, or ``None`` when ``text`` is not a workflow."""
    try:
        loaded = load_yaml(text)
    except yaml.YAMLError:
        return None
    workflow = try_decode(_Workflow, workflow_keys(loaded))
    if workflow is None or workflow.jobs is None:
        return None
    inherited = _id_token_write(workflow.permissions)
    found: list[LoginJob] = []
    called: list[str] = []
    for name, job in workflow.jobs.items():
        if job is None:
            continue
        if job.uses:
            called.append(PurePosixPath(job.uses.split("@", 1)[0]).name)
        login = _login_job(name, job, inherited)
        if login is not None:
            found.append(login)
    return PublishWorkflow(login_jobs=tuple(found), called=tuple(called))


# ---------------------------------------------------------------- policy checklist


@dataclass(frozen=True, slots=True)
class PolicyChecklist:
    """One policy's fields, as the form at ``NUGET_TRUST_URL`` asks for them."""

    policy_owner: str | None
    repository_owner: str
    repository: str
    workflow: str
    environment: str | None
    scopes: tuple[str, ...]
    package_ids: tuple[str, ...]

    @property
    def package_glob(self) -> str:
        return package_glob(self.package_ids)

    def render(self) -> str:
        lines = [
            f"policy owner (nuget.org user or organisation): {self.policy_owner or '? (declare nuget_owner)'}",
            f"repository owner: {self.repository_owner}",
            f"repository: {self.repository}",
            f"workflow file: {PurePosixPath(self.workflow).name}",
            f"environment: {self.environment or '(none)'}",
            f"scopes: {', '.join(self.scopes) or '? (declare scopes)'}",
            f"packages: {self.package_glob}",
            f"at: {NUGET_TRUST_URL}",
            f"note: {POLICY_LIFETIME_NOTE}",
        ]
        return "\n".join(lines)


def package_glob(ids: tuple[str, ...]) -> str:
    """A glob covering every ID: their shared prefix plus ``*``, else the IDs themselves.

    ``Parquet.SourceGenerator`` and ``Parquet.SourceGenerator.Legacy`` share the whole first
    ID, so the glob is ``Parquet.SourceGenerator*``. A prefix that ends mid-word is cut back to
    its last ``.``, so ``Foo.Bar`` and ``Foo.Baz`` give ``Foo.*`` rather than ``Foo.Ba*``.
    """
    if len(ids) == 1:
        return ids[0]
    prefix = os.path.commonprefix([i.lower() for i in ids])
    whole = next((i for i in ids if i.lower() == prefix), None)
    prefix = prefix[: prefix.rfind(".") + 1] if whole is None else whole
    if len(prefix.rstrip(".")) < 4:
        return ", ".join(ids)
    return ids[0][: len(prefix)] + "*"
