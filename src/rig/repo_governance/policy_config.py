"""Load and resolve `config/repo_policies.json`.

Governance rules live in that file, not in Python. Resolution is three layers:
`defaults` applies to every repo, a profile may override any key wholesale,
and `repo_overrides` beats both (no deep merge).
"""

from __future__ import annotations

import fnmatch
import json
import logging
import pathlib
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from functools import lru_cache
from typing import Annotated, Literal

from pydantic import BeforeValidator, TypeAdapter

from rig.repo_governance.agents_md_rules import AgentsMdRules
from rig.repo_governance.fork_policy import ForkSpec
from rig.repo_governance.policy_key import bare_name, names_match
from rig.repo_governance.repo_selection import ManageDecision, RepoSelectionRules, should_manage_repo
from rig.typed_json import load_json

__all__ = (
    "AgentsMdRules",
    "BranchCleanupRules",
    "EnvironmentPolicy",
    "ForkSpec",
    "GitHubAppAccountPolicy",
    "GitHubAppPolicy",
    "GitignoreEntry",
    "LabelSpec",
    "LanguagePreset",
    "ManageDecision",
    "MergeSettings",
    "PagesPolicy",
    "PolicyConfig",
    "PolicyFile",
    "RepoScriptRules",
    "RepoSelectionRules",
    "ScriptBanRules",
    "VercelGatingRules",
    "config_path",
    "load_policy_config",
    "should_manage_repo",
)

_CONFIG_RELATIVE = pathlib.Path("config") / "repo_policies.json"
_log = logging.getLogger(__name__)


def config_path(explicit: str | pathlib.Path | None = None) -> pathlib.Path:
    """Return policy config path (explicit, cwd relative, or repo copy)."""
    if explicit:
        return pathlib.Path(explicit)
    cwd = pathlib.Path.cwd() / _CONFIG_RELATIVE
    return cwd if cwd.is_file() else pathlib.Path(__file__).parents[3] / _CONFIG_RELATIVE


def _is_gitignore_token_satisfied(token: str, lines: set[str]) -> bool:
    """Checks if a gitignore token is satisfied by existing lines in gitignore."""
    if token in lines:
        return True
    stripped = token.strip("/")
    if stripped:
        candidates = {
            token,
            stripped,
            f"{stripped}/",
            f"/{stripped}",
            f"/{stripped}/",
            f"**/{stripped}",
            f"**/{stripped}/",
        }
        if any(c in lines for c in candidates):
            return True
    synonyms = {
        "temp": {"temp", "temp/", "/temp", "/temp/", ".temp", ".temp/", "/.temp", "/.temp/"},
        ".temp": {"temp", "temp/", "/temp", "/temp/", ".temp", ".temp/", "/.temp", "/.temp/"},
        "bin": {"[Bb]in", "[Bb]in/", "**/bin", "**/bin/", "/bin/"},
        "obj": {"[Oo]bj", "[Oo]bj/", "**/obj", "**/obj/", "/obj/"},
        "venv": {".venv", ".venv/", "venv", "venv/", "env", "env/", "ENV", "ENV/"},
        ".venv": {".venv", ".venv/", "venv", "venv/", "env", "env/", "ENV", "ENV/"},
        "node_modules": {"node_modules", "node_modules/", "/node_modules", "/node_modules/", "**/node_modules"},
    }
    if stripped in synonyms and any(t in lines for t in synonyms[stripped]):
        return True
    if token in {"TODO.md", "todo.md"} and "[Tt][Oo][Dd][Oo].md" in lines:
        return True
    return token == ".DS_Store" and any(t in lines for t in (".DS_Store", ".DS_Store*", "**/.DS_Store", "*/.DS_Store"))


type MatchMode = Literal["any", "all"]


@dataclass(frozen=True)
class GitignoreEntry:
    """One required `.gitignore` rule or rule group."""

    name: str
    comment: str
    write: tuple[str, ...]
    satisfied_by: tuple[str, ...]
    match_mode: MatchMode = "any"

    def missing_lines(self, gitignore_text: str) -> list[str]:
        """Return the lines from write that are not satisfied by gitignore_text."""
        lines = {line.split("#")[0].strip() for line in gitignore_text.splitlines()}
        lines.discard("")

        if self.match_mode == "all":
            missing: list[str] = []
            for item in self.write:
                if not _is_gitignore_token_satisfied(item, lines):
                    missing.append(item)
            return missing

        # For any-mode, matching any acceptable token satisfies the entire entry
        if any(_is_gitignore_token_satisfied(token, lines) for token in self.satisfied_by):
            return []
        return list(self.write)

    def is_present(self, gitignore_text: str) -> bool:
        """Return True if this entry is considered satisfied by *gitignore_text*."""
        return len(self.missing_lines(gitignore_text)) == 0

    def render_missing(self, missing: Sequence[str]) -> str:
        """Return the block to append when one or more entries are missing."""
        if not missing:
            return ""
        body = "\n".join(missing)
        return f"\n# {self.comment}\n{body}\n"

    def render(self) -> str:
        """Return the block to append when this entry is missing."""
        return self.render_missing(self.write)


@dataclass(slots=True, frozen=True)
class GitignoreEntrySpec:
    """A `gitignore_entries` item as written: name and `satisfied_by` may be omitted."""

    write: tuple[str, ...] = ()
    name: str = ""
    comment: str = ""
    satisfied_by: tuple[str, ...] | None = None
    match_mode: MatchMode = "any"

    def to_entry(self) -> GitignoreEntry:
        return GitignoreEntry(
            name=self.name or (self.write[0] if self.write else "entry"),
            comment=self.comment,
            write=self.write,
            satisfied_by=self.write if self.satisfied_by is None else self.satisfied_by,
            match_mode=self.match_mode,
        )


@dataclass(frozen=True)
class LanguagePreset:
    """A configured language profile containing file detection and gitignore rules."""

    id: str
    name: str
    aliases: tuple[str, ...] = ()
    detect_files: tuple[str, ...] = ()
    gitignore_entries: tuple[GitignoreEntry, ...] = ()
    script_extensions: tuple[str, ...] = ()
    banned_script_extensions: tuple[str, ...] = ()

    def matches_file(self, filename: str) -> bool:
        """Return True if filename matches any pattern in detect_files."""
        return any(pattern == filename or fnmatch.fnmatch(filename, pattern) for pattern in self.detect_files)


@dataclass(slots=True, frozen=True)
class LanguagePresetSpec:
    """A `language_presets` value as written; its id is the key it sits under."""

    name: str = ""
    aliases: tuple[str, ...] = ()
    detect_files: tuple[str, ...] = ()
    gitignore_entries: tuple[GitignoreEntrySpec, ...] = ()
    script_extensions: tuple[str, ...] = ()
    banned_script_extensions: tuple[str, ...] = ()

    def to_preset(self, preset_id: str) -> LanguagePreset:
        return LanguagePreset(
            id=preset_id,
            name=self.name or preset_id,
            aliases=tuple(a.lower() for a in self.aliases),
            detect_files=self.detect_files,
            gitignore_entries=tuple(e.to_entry() for e in self.gitignore_entries),
            script_extensions=self.script_extensions,
            banned_script_extensions=self.banned_script_extensions,
        )


@dataclass(frozen=True)
class BranchCleanupRules:
    """Which remote branches may be deleted once their work has landed."""

    enabled: bool = True
    #: Never deleted, whatever their merge state.
    protected: tuple[str, ...] = ("main", "master")
    #: Skip branches that still have an open PR.
    protect_open_pr_heads: bool = True
    #: Skip branches under these prefixes — bots recreate and manage their own.
    protect_prefixes: tuple[str, ...] = ()

    def is_protected(self, branch: str) -> bool:
        """Return True if *branch* must never be deleted by cleanup."""
        if branch in self.protected:
            return True
        return any(branch.startswith(prefix) for prefix in self.protect_prefixes)


@dataclass(frozen=True)
class EnvironmentPolicy:
    """A declared GitHub deployment environment and its approval gate.

    ``reviewers`` are logins that must approve a workflow job referencing the
    environment. GitHub returns 404 for protection-rule endpoints when the
    account plan does not serve them; the sync surfaces that as
    ``unavailable`` rather than pretending the gate exists.
    """

    name: str
    wait_timer: int = 0
    reviewers: tuple[str, ...] = ()


@dataclass(slots=True, frozen=True)
class EnvironmentSpec:
    """An `environments` value as written; its name is the key it sits under."""

    wait_timer: int | None = None
    reviewers: tuple[str, ...] = ()

    def to_policy(self, name: str) -> EnvironmentPolicy:
        return EnvironmentPolicy(name=name, wait_timer=self.wait_timer or 0, reviewers=self.reviewers)


@dataclass(frozen=True)
class ScriptBanRules:
    """Rules and exceptions for the estate-wide ban on shell and PowerShell scripts."""

    enabled: bool = True
    banned_extensions: tuple[str, ...] = (".sh", ".bash", ".zsh", ".ps1", ".psm1")
    allowed_exceptions: tuple[str, ...] = ()
    allowed_script_extensions: tuple[str, ...] = ()
    banned_script_extensions: tuple[str, ...] = ()


@dataclass(frozen=True)
class RepoScriptRules:
    """Resolved script rules for a repository."""

    allowed_extensions: tuple[str, ...] = ()
    banned_extensions: tuple[str, ...] = ()


@dataclass(frozen=True)
class VercelGatingRules:
    """Account facts for the Vercel deployment-creation audit."""

    team_id: str = ""
    window_days: int = 3


@dataclass(frozen=True)
class MergeSettings:
    """The merge settings a repo carries on GitHub (`repo_settings`); set by one ``PATCH repos/<repo>``."""

    delete_branch_on_merge: bool = True
    allow_squash_merge: bool = True
    allow_merge_commit: bool = False
    allow_rebase_merge: bool = False
    allow_auto_merge: bool = True


@dataclass(frozen=True)
class LabelSpec:
    """One label of the taxonomy (`labels`); *color* is six hex digits without ``#``."""

    name: str
    color: str
    description: str


@dataclass(frozen=True)
class PagesPolicy:
    """GitHub Pages configuration policy for a repository."""

    enabled: bool = False
    build_type: Literal["workflow", "legacy"] = "workflow"
    cname: str | None = None
    https_enforced: bool = True


@dataclass(frozen=True)
class GitHubAppAccountPolicy:
    """Policy declaring which repos an installed app should be connected to for a specific account."""

    account: str
    required: bool = False
    repos: tuple[str, ...] = ()
    all_repos: bool = False
    disallow_repos: tuple[str, ...] = ()

    @property
    def exclude_repos(self) -> tuple[str, ...]:
        return self.disallow_repos

    def effective_repos(self, available_repos: Sequence[str] = ()) -> tuple[str, ...]:
        """Calculates expected repositories after subtracting disallow_repos."""
        candidates = list(available_repos) if self.all_repos else list(self.repos)
        excluded = set(self.disallow_repos)
        return tuple(r for r in candidates if r not in excluded)


@dataclass(slots=True, frozen=True)
class AppAccountSpec:
    """An app's `accounts` value as written. `exclude_repos` is the older name for `disallow_repos`."""

    required: bool = False
    repos: tuple[str, ...] = ()
    all_repos: bool = False
    disallow_repos: tuple[str, ...] = ()
    exclude_repos: tuple[str, ...] = ()

    def to_policy(self, account: str) -> GitHubAppAccountPolicy:
        return GitHubAppAccountPolicy(
            account=account,
            required=self.required,
            repos=self.repos,
            all_repos=self.all_repos,
            disallow_repos=self.disallow_repos or self.exclude_repos,
        )


def _empty_account_policies() -> dict[str, GitHubAppAccountPolicy]:
    return {}


@dataclass(frozen=True)
class GitHubAppPolicy:
    """A declared GitHub App integration and its account enrollments."""

    slug: str
    name: str
    install_url: str = ""
    config_template: str | None = None
    accounts: dict[str, GitHubAppAccountPolicy] = field(default_factory=_empty_account_policies)


@dataclass(slots=True, frozen=True)
class AppSpec:
    """A `github_apps` value as written; its slug is the key it sits under."""

    name: str = ""
    install_url: str = ""
    config_template: str | None = None
    accounts: dict[str, AppAccountSpec] = field(default_factory=dict[str, AppAccountSpec])

    def to_policy(self, slug: str) -> GitHubAppPolicy:
        return GitHubAppPolicy(
            slug=slug,
            name=self.name or slug,
            install_url=self.install_url or f"https://github.com/apps/{slug}/installations/new",
            config_template=self.config_template or None,
            accounts={acc: spec.to_policy(acc) for acc, spec in self.accounts.items()},
        )


@dataclass(slots=True, frozen=True)
class LicenseSpec:
    type: str = ""


def _null_clears(value: object) -> object:
    """An explicit `null` stops inheritance; the validator never runs for an absent key."""
    return "" if value is None else value


@dataclass(slots=True, frozen=True)
class PolicyLayer:
    """Keys any of `defaults`, a profile or a repo override may set. `None` means "not declared here".

    `expected_owner: null` is kept as `""`, so a profile can say "no expected owner" instead
    of inheriting the default.
    """

    expected_owner: Annotated[str | None, BeforeValidator(_null_clears)] = None
    allowed_owners: tuple[str, ...] | None = None
    expected_base_branch: str | None = None
    copyright_holder: str | None = None
    contact_email: str | None = None
    license: LicenseSpec | str | None = None
    languages: tuple[str, ...] | None = None
    gitignore_entries: tuple[GitignoreEntrySpec, ...] | None = None
    repo_settings: MergeSettings | None = None
    labels: tuple[LabelSpec, ...] | None = None
    branch_cleanup: BranchCleanupRules | None = None
    repo_selection: RepoSelectionRules | None = None
    environments: dict[str, EnvironmentSpec] | None = None
    required_checks: tuple[str, ...] | None = None
    agents_md_hygiene: AgentsMdRules | None = None
    vercel: VercelGatingRules | None = None
    pages: PagesPolicy | None = None
    script_ban: ScriptBanRules | None = None
    fork: ForkSpec | None = None


@dataclass(slots=True, frozen=True)
class Profile(PolicyLayer):
    """A named group of repos and the layer of settings they share."""

    repos: tuple[str, ...] = ()
    policies: tuple[str, ...] = ()

    def identity(self) -> PolicyLayer:
        """Who owns a repo: all an unregistered repo takes from its owner's profile, so never its rules."""
        return PolicyLayer(
            expected_owner=self.expected_owner,
            allowed_owners=self.allowed_owners,
            copyright_holder=self.copyright_holder,
            contact_email=self.contact_email,
            license=self.license,
        )


@dataclass(slots=True, frozen=True)
class PolicyFile:
    """`config/repo_policies.json` as decoded. Unknown keys (`$schema`, `description`) are ignored."""

    defaults: PolicyLayer = field(default_factory=PolicyLayer)
    profiles: dict[str, Profile] = field(default_factory=dict[str, Profile])
    repo_overrides: dict[str, PolicyLayer] = field(default_factory=dict[str, PolicyLayer])
    language_presets: dict[str, LanguagePresetSpec] = field(default_factory=dict[str, LanguagePresetSpec])
    github_apps: dict[str, AppSpec] = field(default_factory=dict[str, AppSpec])
    owner_profiles: dict[str, str] = field(default_factory=dict[str, str])
    """GitHub owner -> the profile its repos fall back to while no profile names them."""


_POLICY_FILE: TypeAdapter[PolicyFile] = TypeAdapter(PolicyFile)


@dataclass(frozen=True)
class PolicyConfig:
    """The parsed policy file, with per-repo resolution."""

    file: PolicyFile = field(default_factory=PolicyFile)

    @classmethod
    def parse(cls, raw: object) -> PolicyConfig:
        """Decode a parsed JSON document.

        Raises `pydantic.ValidationError` on a misshapen file, and `ValueError` when
        ``owner_profiles`` names a profile the file does not declare.
        """
        file = _POLICY_FILE.validate_python(raw)
        unknown = {owner: name for owner, name in file.owner_profiles.items() if name not in file.profiles}
        if unknown:
            shown = ", ".join(f"{owner} -> {name}" for owner, name in sorted(unknown.items()))
            raise ValueError(f"owner_profiles names undeclared profile(s): {shown}")
        return cls(file)

    # -- profile lookup ---------------------------------------------------

    def profile_for(self, repo: str) -> str | None:
        """Return the profile name declaring *repo*, or None.

        *repo* may be a bare name or an ``owner/name`` slug; only the last
        segment is matched, ignoring case, since the config lists bare names.
        """
        for name, profile in self.file.profiles.items():
            if names_match(profile.repos, repo):
                return name
        return None

    def owner_profile_for(self, repo: str) -> str | None:
        """Return the profile *repo*'s owner falls back to, or None.

        Only an ``owner/name`` slug carries an owner. A repo that a profile names
        never falls back: registration is explicit and wins.
        """
        if "/" not in repo or self.profile_for(repo) is not None:
            return None
        owner = repo.split("/", 1)[0].lower()
        for name, profile in self.file.owner_profiles.items():
            if name.lower() == owner and profile in self.file.profiles:
                return profile
        return None

    def _layers(self, repo: str) -> list[PolicyLayer]:
        """The layers that apply to *repo*, most specific first.

        An unregistered repo's middle layer is its owner's `Profile.identity` (``owner_profiles``).
        """
        layers: list[PolicyLayer] = []
        override = next((o for n, o in self.file.repo_overrides.items() if bare_name(n) == bare_name(repo)), None)
        if override is not None:
            layers.append(override)
        profile_name = self.profile_for(repo)
        if profile_name is not None:
            layers.append(self.file.profiles[profile_name])
        else:
            inferred = self.owner_profile_for(repo)
            if inferred is not None:
                layers.append(self.file.profiles[inferred].identity())
        layers.append(self.file.defaults)
        return layers

    def _resolve[T](self, repo: str, pick: Callable[[PolicyLayer], T | None]) -> T | None:
        """Return the first layer's value for one key: repo override, then profile, then defaults."""
        for layer in self._layers(repo):
            value = pick(layer)
            if value is not None:
                return value
        return None

    # -- typed accessors --------------------------------------------------

    def allowed_owners(self, repo: str) -> tuple[str, ...]:
        """Return allowed GitHub owners/orgs for this repo/profile."""
        owners = self._resolve(repo, lambda layer: layer.allowed_owners)
        return owners if owners is not None else ("rtkelly13", "rtkelly-labs")

    def expected_owner(self, repo: str) -> str | None:
        """Return specific expected owner/org if explicitly configured."""
        return self._resolve(repo, lambda layer: layer.expected_owner) or None

    def expected_base_branch(self, repo: str) -> str:
        """Return expected base/default branch for *repo*, falling back to 'main'."""
        return self._resolve(repo, lambda layer: layer.expected_base_branch) or "main"

    def copyright_holder(self, repo: str) -> str:
        """Return expected copyright holder for *repo*, falling back to 'Ryan Kelly'."""
        return self._resolve(repo, lambda layer: layer.copyright_holder) or "Ryan Kelly"

    def contact_email(self, repo: str) -> str:
        """Return contact email for *repo*, falling back to 'ryan@ryankelly.dev'."""
        return self._resolve(repo, lambda layer: layer.contact_email) or "ryan@ryankelly.dev"

    def license_type(self, repo: str) -> str:
        """Return expected license type for *repo*, falling back to 'MIT'."""
        lic = self._resolve(repo, lambda layer: layer.license)
        if isinstance(lic, LicenseSpec):
            return lic.type or "MIT"
        return lic or "MIT"

    def repos_for_org(self, org: str) -> tuple[str, ...]:
        """Return repository names explicitly registered under *org*."""
        org_lower = org.lower()
        repos: list[str] = []
        for profile in self.file.profiles.values():
            if profile.expected_owner and profile.expected_owner.lower() == org_lower:
                repos.extend(profile.repos)
        for repo_name, override in self.file.repo_overrides.items():
            expected = override.expected_owner
            if expected and expected.lower() == org_lower and repo_name not in repos:
                repos.append(repo_name)
        return tuple(repos)

    def is_repo_in_org(self, repo_name: str, org: str) -> bool:
        """Return True if *repo_name* belongs to *org*."""
        org_lower = org.lower()
        bare = repo_name.rsplit("/", 1)[-1]
        if "/" in repo_name:
            owner = repo_name.split("/", 1)[0]
            if owner.lower() == org_lower:
                return True
        expected = self.expected_owner(bare)
        if expected and expected.lower() == org_lower:
            return True
        return names_match(self.repos_for_org(org), bare)

    def language_presets(self) -> dict[str, LanguagePreset]:
        """Return available language presets keyed by preset ID."""
        return {pid: spec.to_preset(pid) for pid, spec in self.file.language_presets.items()}

    def detect_languages(self, repo_path: pathlib.Path) -> tuple[str, ...]:
        """Detect language presets based on files present in the repo root or depth-1 dirs."""
        if not repo_path.is_dir():
            return ()
        presets = self.language_presets()
        if not presets:
            return ()

        try:
            candidates: list[str] = [child.name for child in repo_path.iterdir() if not child.name.startswith(".git")]
            # Also check immediate subdirectories (depth 1) for split full-stack repos (e.g. backend/, frontend/, src/)
            ignore_dirs = (".", "node_modules", "temp", "vendor", "dist", "bin", "obj")
            for child in repo_path.iterdir():
                if child.is_dir() and not child.name.startswith(ignore_dirs):
                    try:
                        for grandchild in child.iterdir():
                            candidates.append(grandchild.name)
                    except OSError:
                        pass
        except OSError:
            return ()

        detected: list[str] = []
        for preset_id, preset in presets.items():
            if any(preset.matches_file(fname) for fname in candidates):
                detected.append(preset_id)
        return tuple(detected)

    def normalize_language(self, lang: str) -> str:
        """Resolve a language alias or name to its canonical preset ID."""
        cleaned = lang.strip().lower()
        presets = self.language_presets()
        if cleaned in presets:
            return cleaned
        for preset_id, preset in presets.items():
            if cleaned == preset.name.lower() or cleaned in preset.aliases:
                return preset_id
        return cleaned

    def resolve_languages(
        self,
        repo: str,
        repo_path: pathlib.Path | None = None,
        explicit_languages: Sequence[str] | None = None,
    ) -> tuple[str, ...]:
        """Resolve languages for a repo: explicit arg > repo policy > auto-detect."""
        if explicit_languages:
            resolved: list[str] = []
            for lang in explicit_languages:
                for part in lang.split(","):
                    norm = self.normalize_language(part)
                    if norm and norm not in resolved:
                        resolved.append(norm)
            return tuple(resolved)

        configured = self._resolve(repo, lambda layer: layer.languages)
        if configured:
            return tuple(self.normalize_language(x) for x in configured)

        if repo_path and repo_path.is_dir():
            return self.detect_languages(repo_path)

        return ()

    def gitignore_entries(
        self,
        repo: str,
        repo_path: pathlib.Path | None = None,
        languages: Sequence[str] | None = None,
    ) -> tuple[GitignoreEntry, ...]:
        """Return the `.gitignore` entries required of *repo*.

        Combines default/profile base entries with language-specific entries
        for resolved languages.
        """
        specs = self._resolve(repo, lambda layer: layer.gitignore_entries) or ()
        entries = [spec.to_entry() for spec in specs]
        seen_names = {e.name for e in entries}

        resolved_langs = self.resolve_languages(repo, repo_path=repo_path, explicit_languages=languages)
        presets = self.language_presets()

        for lang in resolved_langs:
            preset = presets.get(lang)
            if not preset:
                continue
            for entry in preset.gitignore_entries:
                if entry.name not in seen_names:
                    entries.append(entry)
                    seen_names.add(entry.name)

        return tuple(entries)

    def repo_settings(self, repo: str) -> MergeSettings:
        """Return the merge settings *repo* should carry on GitHub."""
        return self._resolve(repo, lambda layer: layer.repo_settings) or MergeSettings()

    def labels(self, repo: str) -> tuple[LabelSpec, ...]:
        """Return the label taxonomy for *repo*; empty means labels are not governed."""
        return self._resolve(repo, lambda layer: layer.labels) or ()

    def fork(self, repo: str) -> ForkSpec | None:
        """Return the upstream *repo* tracks, or None when it is not a managed fork."""
        return self._resolve(repo, lambda layer: layer.fork)

    def branch_cleanup(self, repo: str) -> BranchCleanupRules:
        """Return the branch-cleanup rules for *repo*."""
        return self._resolve(repo, lambda layer: layer.branch_cleanup) or BranchCleanupRules()

    def repo_selection(self, repo: str) -> RepoSelectionRules:
        """Return the selection rules for *repo*."""
        return self._resolve(repo, lambda layer: layer.repo_selection) or RepoSelectionRules()

    def environments(self, repo: str) -> dict[str, EnvironmentPolicy]:
        """Return the declared deployment environments for *repo*, keyed by name.

        An empty dict means "declare nothing" — no environment is created and
        none is reported as drift. Environments are opt-in per profile because
        most repos in the estate do not publish.
        """
        specs = self._resolve(repo, lambda layer: layer.environments) or {}
        return {name: spec.to_policy(name) for name, spec in specs.items()}

    def is_registered(self, repo: str) -> bool:
        """Return True if *repo* is named in a profile — an explicit opt-in."""
        return self.profile_for(repo) is not None

    def required_checks(self, repo: str) -> tuple[str, ...]:
        """Return the profile's required check names for *repo*."""
        return self._resolve(repo, lambda layer: layer.required_checks) or ()

    def agents_md(self, repo: str) -> AgentsMdRules:
        """Return the `AGENTS.md` hygiene rules for *repo*."""
        return self._resolve(repo, lambda layer: layer.agents_md_hygiene) or AgentsMdRules()

    def vercel_gating(self, repo: str) -> VercelGatingRules:
        """Return the Vercel account facts used by the creation-gate audit."""
        return self._resolve(repo, lambda layer: layer.vercel) or VercelGatingRules()

    def pages(self, repo: str) -> PagesPolicy:
        """Return the GitHub Pages policy for *repo*."""
        return self._resolve(repo, lambda layer: layer.pages) or PagesPolicy()

    def script_ban(self, repo: str) -> ScriptBanRules:
        """Return the script ban rules and exceptions for *repo*."""
        return self._resolve(repo, lambda layer: layer.script_ban) or ScriptBanRules()

    def script_rules_for_repo(
        self,
        repo: str,
        repo_path: pathlib.Path | None = None,
    ) -> RepoScriptRules:
        """Return resolved script rules for repo based on resolved languages."""
        base_ban = self.script_ban(repo)
        if base_ban.allowed_script_extensions or base_ban.banned_script_extensions:
            return RepoScriptRules(
                allowed_extensions=base_ban.allowed_script_extensions,
                banned_extensions=base_ban.banned_script_extensions,
            )
        langs = self.resolve_languages(repo, repo_path=repo_path)
        presets = self.language_presets()
        allowed: list[str] = []
        banned: list[str] = []
        has_python = False
        for lang in langs:
            norm = self.normalize_language(lang)
            if norm == "python":
                has_python = True
            preset = presets.get(norm)
            if preset:
                for ext in preset.script_extensions:
                    if ext not in allowed:
                        allowed.append(ext)
                for ext in preset.banned_script_extensions:
                    if ext not in banned:
                        banned.append(ext)
        if has_python and ".py" in banned:
            banned.remove(".py")
        return RepoScriptRules(
            allowed_extensions=tuple(allowed),
            banned_extensions=tuple(banned),
        )

    def has_policy(self, repo: str, policy: str) -> bool:
        """Return True if *repo*'s profile declares *policy* in its policies list."""
        profile_name = self.profile_for(repo)
        if not profile_name:
            return False
        return policy in self.file.profiles[profile_name].policies

    def github_apps(self) -> dict[str, GitHubAppPolicy]:
        """Return declared GitHub Apps indexed by slug."""
        return {slug: spec.to_policy(slug) for slug, spec in self.file.github_apps.items()}

    def apps_for_account(self, account: str) -> tuple[GitHubAppPolicy, ...]:
        """Return GitHub Apps declared for *account* (organization or user)."""
        acc_lower = account.lower()
        result: list[GitHubAppPolicy] = []
        for app in self.github_apps().values():
            for acc in app.accounts:
                if acc.lower() == acc_lower:
                    result.append(app)
                    break
        return tuple(result)

    def apps_for_repo(self, repo: str, account: str) -> tuple[GitHubAppPolicy, ...]:
        """Return GitHub Apps expected to be enabled on *repo* under *account*."""
        acc_lower = account.lower()
        result: list[GitHubAppPolicy] = []
        for app in self.apps_for_account(account):
            for acc_name, acc_policy in app.accounts.items():
                if acc_name.lower() == acc_lower:
                    if names_match(acc_policy.disallow_repos, repo):
                        break
                    if acc_policy.all_repos or names_match(acc_policy.repos, repo):
                        result.append(app)
                    break
        return tuple(result)


@lru_cache(maxsize=8)
def _load_cached(path_str: str) -> PolicyConfig:
    path = pathlib.Path(path_str)
    if not path.is_file():
        return PolicyConfig()
    try:
        raw = load_json(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        # A malformed policy file must not take the CLI down; callers fall back
        # to built-in behaviour and the audit surfaces the drift.
        _log.warning("Ignoring unreadable policy file %s: %s", path, exc)
        return PolicyConfig()
    # A schema error propagates: falling back would silently drop protections
    # such as branch-cleanup prefixes, not just the one bad field.
    return PolicyConfig.parse(raw)


def load_policy_config(explicit: str | pathlib.Path | None = None) -> PolicyConfig:
    """Load the policy config, cached per resolved path."""
    return _load_cached(str(config_path(explicit).resolve()))
