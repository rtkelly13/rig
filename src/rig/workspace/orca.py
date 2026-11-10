"""`workspace orca doctor` and `workspace orca sync`: audit and manage Orca per repo.

Orca keeps its settings in a SQLite document store that it holds open and rewrites while it
runs. For diagnosis, settings are read from it read-only, or from the newest
`profile-state.db.backup.*.db` beside it when it cannot be read. Rig never writes to either. Live
repos come from `orca repo list --json`.

For mutations, see `rig.workspace.orca_rpc`. Every entry point takes an optional Orca user-data
folder, so a second instance (such as `%APPDATA%/orca-personal`) can be audited and synced.
"""

from __future__ import annotations

import os
import sqlite3
from collections.abc import Callable
from contextlib import closing
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from typing import Annotated

from pydantic import Field

from rig import roots
from rig.typed_json import decode, decode_json, try_decode_json
from rig.workspace.orca_agent_permissions import (
    AgentCheck,
    AgentPermissionsPolicy,
    custom_args_checks,
    launch_checks,
    permission_checks,
)
from rig.workspace.orca_agent_sleep import AgentSleepPolicy, agent_sleep_check
from rig.workspace.orca_hooks import (
    ORCA_YAML,
    HookSettings,
    ScriptKind,
    Scripts,
    defer_to_orca_yaml,
    read_orca_yaml,
    resolve,
)
from rig.workspace.orca_rpc import (
    CommandRunner,
    OrcaRuntimeMetadata,
    RepoRpcUpdates,
    RepoUpdateParams,
    RpcCaller,
    RpcRequest,
    Visibility,
    bound_rpc,
    build_repo_icon_payload,
    default_user_data_dir,
    orca_runner,
    orca_runtime_metadata_path,
    read_orca_runtime_metadata,
    run_orca,
    send_orca_rpc,
    set_base_ref,
    set_visibility,
    update_repo,
)
from rig.workspace.packages import Status

__all__ = [
    "policy_repo_path",
    "CONFIG_RELATIVE_PATH",
    "ORCA_LUCIDE_ICONS",
    "CommandRunner",
    "OrcaFinding",
    "OrcaPolicy",
    "OrcaRepo",
    "OrcaRepoIcon",
    "OrcaRuntimeMetadata",
    "OrcaSettings",
    "OrcaSnapshot",
    "RepoPolicy",
    "RepoRpcUpdates",
    "RepoUpdateParams",
    "RpcCaller",
    "RpcRequest",
    "SettingsCopy",
    "SyncAction",
    "Visibility",
    "build_repo_icon_payload",
    "diagnose",
    "icon_matches",
    "icon_renders",
    "live_repos",
    "load_policy",
    "newest_backup",
    "orca_profile_dir",
    "orca_runner",
    "read_orca_runtime_metadata",
    "read_settings",
    "read_settings_copy",
    "run_orca",
    "send_orca_rpc",
    "sync_repos",
    "agent_checks",
    "take_snapshot",
    "set_base_ref",
    "set_visibility",
    "update_repo",
]

CONFIG_RELATIVE_PATH = Path("config") / "orca.json"
STALE_AFTER_HOURS = 24
_BACKUP_GLOB = "profile-state.db.backup.*.db"


# ---------------------------------------------------------------- policy


@dataclass(slots=True, frozen=True)
class RepoPolicy:
    path: str | None = None
    setup: str | None = None
    archive: str | None = None
    external_worktree_visibility: Visibility | None = None
    base_ref: str | None = None
    worktree_base_path: str | None = None
    color: str | None = None
    icon: str | None = None


@dataclass(slots=True, frozen=True)
class OrcaPolicy:
    """What `config/orca.json` declares. Agents and repos it leaves out are not checked."""

    agent_args: dict[str, str] = field(default_factory=dict[str, str])
    agent_permissions: AgentPermissionsPolicy | None = None
    workspace_dir: str | None = None
    agent_sleep: AgentSleepPolicy | None = None
    repos: dict[str, RepoPolicy] = field(default_factory=dict[str, RepoPolicy])


def load_policy(path: Path) -> OrcaPolicy:
    """Decode `config/orca.json`; a malformed file raises ``ValueError``."""
    return decode_json(OrcaPolicy, path.read_text(encoding="utf-8"))


# ---------------------------------------------------------------- Orca state


@dataclass(slots=True, frozen=True)
class OrcaSettings:
    agent_default_args: Annotated[dict[str, str] | None, Field(alias="agentDefaultArgs")] = None
    workspace_dir: Annotated[str | None, Field(alias="workspaceDir")] = None
    agent_sleep: Annotated[bool | None, Field(alias="experimentalAgentHibernation")] = None
    agent_sleep_idle_ms: Annotated[int | None, Field(alias="agentHibernationIdleMs")] = None
    permission_default: Annotated[str | None, Field(alias="agentPermissionDefault")] = None
    permission_overrides: Annotated[dict[str, str] | None, Field(alias="agentPermissionOverrides")] = None


@dataclass(slots=True, frozen=True)
class OrcaRepoIcon:
    type: str = ""
    name: str = ""
    emoji: str = ""
    src: str = ""
    source: str = ""
    label: str = ""


@dataclass(slots=True, frozen=True)
class OrcaRepo:
    path: str
    display_name: Annotated[str, Field(alias="displayName")] = ""
    hook_settings: Annotated[HookSettings | None, Field(alias="hookSettings")] = None
    external_worktree_visibility: Annotated[str | None, Field(alias="externalWorktreeVisibility")] = None
    worktree_base_ref: Annotated[str | None, Field(alias="worktreeBaseRef")] = None
    worktree_base_path: Annotated[str | None, Field(alias="worktreeBasePath")] = None
    badge_color: Annotated[str | None, Field(alias="badgeColor")] = None
    repo_icon: Annotated[OrcaRepoIcon | None, Field(alias="repoIcon")] = None

    @property
    def base_ref(self) -> str:
        return self.worktree_base_ref or ""

    @property
    def color(self) -> str:
        return self.badge_color or ""

    @property
    def icon_identifier(self) -> str:
        if not self.repo_icon:
            return ""
        if self.repo_icon.type == "lucide":
            return self.repo_icon.name
        if self.repo_icon.type == "emoji":
            return self.repo_icon.emoji
        if self.repo_icon.type == "image":
            return self.repo_icon.src
        return ""


@dataclass(slots=True, frozen=True)
class _RepoList:
    repos: tuple[OrcaRepo, ...] = ()


@dataclass(slots=True, frozen=True)
class _RepoListReply:
    result: _RepoList | None = None


@dataclass(slots=True, frozen=True)
class SettingsCopy:
    source: Path
    saved: datetime
    settings: OrcaSettings


@dataclass(slots=True, frozen=True)
class OrcaSnapshot:
    """Orca as last saved. ``None`` fields mean that half could not be read."""

    settings: SettingsCopy | None
    repos: tuple[OrcaRepo, ...] | None
    #: Where the settings backup was looked for; None means the default instance's profile.
    profile_dir: Path | None = None
    #: What the running Orca launches each agent with (permission flags plus custom arguments), or
    #: None when it could not be asked.
    launch_args: dict[str, str] | None = None


def orca_profile_dir(user_data: Path | None = None) -> Path:
    """An Orca instance's local profile folder; for the default instance ``ORCA_PROFILE_DIR`` overrides it."""
    if user_data is not None:
        return user_data / "profiles" / "local-default"
    configured = os.environ.get("ORCA_PROFILE_DIR")
    if configured:
        return Path(configured).expanduser()
    return default_user_data_dir() / "profiles" / "local-default"


def newest_backup(profile_dir: Path) -> Path | None:
    backups = [path for path in profile_dir.glob(_BACKUP_GLOB) if path.is_file()]
    return max(backups, key=_mtime) if backups else None


def _mtime(path: Path) -> float:
    return path.stat().st_mtime


_LIVE_DB = "profile-state.db"


def read_settings(db_path: Path, *, live: bool = False) -> OrcaSettings | None:
    """The ``settings`` document of a profile-state database, or None when absent or unreadable.

    A ``live`` database, which Orca may be writing, is not opened as immutable, so its log is read.
    """
    if not db_path.is_file():
        return None
    mode = "mode=ro" if live else "mode=ro&immutable=1"
    try:
        with closing(sqlite3.connect(f"file:{db_path.as_posix()}?{mode}", uri=True)) as db:
            rows = db.execute("select payload from profile_state_documents where domain = 'settings'")
            payloads = decode(list[list[str]], rows.fetchall())
    except (sqlite3.Error, ValueError):
        return None
    return try_decode_json(OrcaSettings, payloads[0][0]) if payloads and payloads[0] else None


def read_settings_copy(profile_dir: Path) -> SettingsCopy | None:
    """Orca's settings from its live database, else from its newest backup."""
    live = profile_dir / _LIVE_DB
    if (settings := read_settings(live, live=True)) is not None:
        # Orca writes through SQLite's write-ahead log, so the log holds the latest save.
        wal = live.with_name(f"{_LIVE_DB}-wal")
        saved = max(_mtime(path) for path in (live, wal) if path.is_file())
        return SettingsCopy(source=live, saved=datetime.fromtimestamp(saved, tz=UTC), settings=settings)
    backup = newest_backup(profile_dir)
    if backup is None:
        return None
    settings = read_settings(backup)
    if settings is None:
        return None
    saved = datetime.fromtimestamp(backup.stat().st_mtime, tz=UTC)
    return SettingsCopy(source=backup, saved=saved, settings=settings)


def live_repos(run: CommandRunner = run_orca) -> tuple[OrcaRepo, ...] | None:
    out = run(["repo", "list", "--json"])
    reply = try_decode_json(_RepoListReply, out) if out else None
    return reply.result.repos if reply and reply.result else None


def take_snapshot(
    profile_dir: Path | None = None,
    run: CommandRunner = run_orca,
    launch: Callable[[], dict[str, str] | None] | None = None,
) -> OrcaSnapshot:
    """Orca's saved settings and live repos; with ``launch``, also what it launches each agent with."""
    folder = profile_dir or orca_profile_dir()
    return OrcaSnapshot(
        read_settings_copy(folder), live_repos(run), profile_dir=folder, launch_args=launch() if launch else None
    )


# ---------------------------------------------------------------- icon helper

# The lucide icons Orca's repo-icon picker offers. Its renderer looks names up in this list exactly
# and draws a folder for anything else, and `repo.update` drops names that are not alphanumeric.
ORCA_LUCIDE_ICONS: frozenset[str] = frozenset(
    {
        "Folder", "Code2", "SquareTerminal", "Bot", "Package", "Database", "Globe", "Server", "Cpu", "Layers",
        "Braces", "Rocket", "Wrench", "Briefcase", "Building2", "Palette", "Gauge", "Sparkles", "Shapes", "Box",
    }
)  # fmt: skip


def icon_renders(icon: str) -> bool:
    """Whether Orca draws ``icon`` as given: an emoji, an image URL, or a lucide name from its picker."""
    payload = build_repo_icon_payload(icon)
    return payload.type != "lucide" or payload.name in ORCA_LUCIDE_ICONS


def icon_matches(want: str, repo: OrcaRepo) -> bool:
    if repo.repo_icon is None:
        return False
    want_payload = build_repo_icon_payload(want)
    if want_payload.type != repo.repo_icon.type:
        return False
    if want_payload.type == "lucide":
        return want_payload.name == repo.repo_icon.name
    if want_payload.type == "emoji":
        return want_payload.emoji == repo.repo_icon.emoji
    if want_payload.type == "image":
        return want_payload.src == repo.repo_icon.src
    return False


# ---------------------------------------------------------------- diagnosis


@dataclass(slots=True, frozen=True)
class OrcaFinding:
    subject: str
    check: str
    status: Status
    detail: str
    fix: str = ""


def diagnose(
    policy: OrcaPolicy,
    snapshot: OrcaSnapshot,
    *,
    estate_root: Path,
    managed_projects: tuple[str, ...] = (),
    now: datetime | None = None,
) -> list[OrcaFinding]:
    """Every difference between the policy and Orca, plus local managed projects Orca has not registered."""
    profile_dir = snapshot.profile_dir or orca_profile_dir()
    findings = _settings_findings(policy, snapshot.settings, profile_dir, now or datetime.now(tz=UTC))
    findings += [_check_finding(c) for c in agent_checks(policy, snapshot)]
    if policy.agent_permissions is not None and snapshot.launch_args is None:
        findings.append(OrcaFinding("agents", "launch args", Status.SKIP, "Orca is not running to ask"))
    findings += _repo_findings(policy, snapshot.repos, estate_root, managed_projects)
    return findings


@dataclass(slots=True, frozen=True)
class _WantedScript:
    kind: ScriptKind
    script: str


def _wanted_scripts(want: RepoPolicy) -> list[_WantedScript]:
    wanted = [_WantedScript("setup", want.setup or ""), _WantedScript("archive", want.archive or "")]
    present = [want.setup is not None, want.archive is not None]
    return [_WantedScript(w.kind, w.script.strip()) for w, keep in zip(wanted, present, strict=True) if keep]


def _script_finding(
    name: str, kind: ScriptKind, want: str, local: HookSettings | None, shared: Scripts | None
) -> OrcaFinding:
    source = resolve(kind, local, shared)
    subject, check = f"repo {name}", f"{kind} script"
    if source.shared == want:
        if source.policy != "local-only" and source.effective == want:
            return OrcaFinding(subject, check, Status.OK, f'"{want}" from {ORCA_YAML}')
        return OrcaFinding(
            subject,
            check,
            Status.WARN if source.effective == want else Status.FAIL,
            f'Orca\'s local script "{source.local}" overrides {ORCA_YAML}',
            "run `workspace orca sync --apply` to clear the local script",
        )
    commit_fix = f"commit {ORCA_YAML} to {name} declaring scripts.{kind}: {want}"
    if source.effective == want:
        return OrcaFinding(
            subject, check, Status.WARN, f'"{want}" is set only in Orca\'s local settings, not {ORCA_YAML}', commit_fix
        )
    shared_note = f'{ORCA_YAML} has "{source.shared}"' if source.shared else f"not in {ORCA_YAML}"
    return OrcaFinding(subject, check, Status.FAIL, f'"{source.effective}" ({shared_note}), want "{want}"', commit_fix)


def _settings_findings(
    policy: OrcaPolicy, copy: SettingsCopy | None, profile_dir: Path, now: datetime
) -> list[OrcaFinding]:
    if copy is None:
        return [
            OrcaFinding(
                "settings",
                "source",
                Status.SKIP,
                f"no readable {_BACKUP_GLOB} in {profile_dir.as_posix()}",
                "start Orca once so it writes a settings backup",
            )
        ]
    age_hours = (now - copy.saved).total_seconds() / 3600
    stale = age_hours > STALE_AFTER_HOURS
    findings = [
        OrcaFinding(
            "settings",
            "source",
            Status.WARN if stale else Status.OK,
            f"{copy.source.name}, saved {age_hours:.1f}h ago",
            "Orca writes these backups as it saves; recent Settings changes may not show yet" if stale else "",
        )
    ]
    if policy.workspace_dir is not None:
        want_dir = Path(policy.workspace_dir).expanduser()
        have_dir = Path(copy.settings.workspace_dir).expanduser() if copy.settings.workspace_dir else None
        ok = have_dir == want_dir
        findings.append(
            OrcaFinding(
                "settings",
                "workspace dir",
                Status.OK if ok else Status.FAIL,
                (have_dir.as_posix() if have_dir else "unset") + ("" if ok else f", want {want_dir.as_posix()}"),
                "" if ok else f"Orca Settings: set the workspace folder to {want_dir.as_posix()}",
            )
        )
    if policy.agent_sleep is not None:
        findings.append(_agent_sleep_finding(policy.agent_sleep, copy.settings))
    return findings


def _agent_sleep_finding(want: AgentSleepPolicy, settings: OrcaSettings) -> OrcaFinding:
    """Agent sleep against the policy. The runtime cannot set it, so the fix is Orca's Settings page."""
    check = agent_sleep_check(want, settings.agent_sleep, settings.agent_sleep_idle_ms)
    if check.ok:
        return OrcaFinding("settings", "agent sleep", Status.OK, check.have)
    return OrcaFinding("settings", "agent sleep", Status.FAIL, f"{check.have}, want {check.want}", check.fix)


def agent_checks(policy: OrcaPolicy, snapshot: OrcaSnapshot, *, launch: bool = True) -> list[AgentCheck]:
    """Custom arguments and permission modes from the backup, and launch arguments from the running Orca."""
    stored = snapshot.settings.settings if snapshot.settings else None
    checks = custom_args_checks(policy.agent_args, stored.agent_default_args or {}) if stored else []
    if (want := policy.agent_permissions) is None:
        return checks
    if stored is not None:
        overrides = stored.permission_overrides or {}
        checks += permission_checks(want, list(overrides), stored.permission_default, overrides)
    if launch and snapshot.launch_args is not None:
        checks += launch_checks(want, policy.agent_args, snapshot.launch_args)
    return checks


def _check_finding(check: AgentCheck) -> OrcaFinding:
    if check.ok:
        return OrcaFinding(check.subject, check.check, Status.OK, check.have or "(none)")
    return OrcaFinding(check.subject, check.check, Status.FAIL, f'"{check.have}", want "{check.want}"', check.fix)


def policy_repo_path(path: str, code_root: Path | None = None) -> Path:
    """A repo path from the policy; `~/code/...` lives under this machine's code root (`rig.roots`).

    The policy is shared across machines and spells every repo as `~/code/<group>/<name>`, but a
    machine may keep its clones elsewhere (such as `C:/code` on Windows).
    """
    parts = Path(path).parts
    if parts[:2] == ("~", "code"):
        return (code_root or roots.code_root()).joinpath(*parts[2:])
    return Path(path).expanduser()


def _repo_findings(
    policy: OrcaPolicy,
    repos: tuple[OrcaRepo, ...] | None,
    estate_root: Path,
    managed_projects: tuple[str, ...],
) -> list[OrcaFinding]:
    if repos is None:
        return [OrcaFinding("repos", "source", Status.SKIP, "`orca repo list --json` failed", "run `orca open`")]
    by_name = {repo.display_name: repo for repo in repos}
    findings: list[OrcaFinding] = []
    for name, want in sorted(policy.repos.items()):
        repo = by_name.get(name)
        if repo is None:
            resolved_path = policy_repo_path(want.path) if want.path else estate_root / name
            findings.append(
                OrcaFinding(
                    f"repo {name}",
                    "registered",
                    Status.FAIL,
                    "named in config/orca.json but not registered in Orca",
                    f"orca repo add --path {resolved_path.as_posix()}",
                )
            )
            continue
        findings += _repo_policy_findings(name, repo, want)
    registered = {Path(repo.path) for repo in repos}
    for name in managed_projects:
        path = estate_root / name
        if (path / ".git").exists() and path not in registered and name not in policy.repos:
            findings.append(
                OrcaFinding(
                    f"repo {name}",
                    "registered",
                    Status.WARN,
                    "managed project cloned locally but not registered in Orca",
                    f"orca repo add --path {path.as_posix()}",
                )
            )
    return findings


def _repo_policy_findings(name: str, repo: OrcaRepo, want: RepoPolicy) -> list[OrcaFinding]:
    findings: list[OrcaFinding] = []
    shared = read_orca_yaml(Path(repo.path))
    findings += [_script_finding(name, w.kind, w.script, repo.hook_settings, shared) for w in _wanted_scripts(want)]
    if want.external_worktree_visibility is not None:
        have = repo.external_worktree_visibility or "inherit"
        ok = have == want.external_worktree_visibility
        findings.append(
            OrcaFinding(
                f"repo {name}",
                "external worktrees",
                Status.OK if ok else Status.FAIL,
                have + ("" if ok else f", want {want.external_worktree_visibility}"),
                ""
                if ok
                else f"orca repo set --repo path:{Path(repo.path).as_posix()} "
                f"--external-worktree-visibility {want.external_worktree_visibility}",
            )
        )
    if want.base_ref is not None:
        ok = repo.base_ref == want.base_ref
        findings.append(
            OrcaFinding(
                f"repo {name}",
                "base ref",
                Status.OK if ok else Status.FAIL,
                (repo.base_ref or "(none)") + ("" if ok else f", want {want.base_ref}"),
                "" if ok else f"orca repo set-base-ref --repo path:{Path(repo.path).as_posix()} --ref {want.base_ref}",
            )
        )
    if want.worktree_base_path is not None:
        have = (repo.worktree_base_path or "").strip()
        ok = have == want.worktree_base_path.strip()
        findings.append(
            OrcaFinding(
                f"repo {name}",
                "worktree location",
                Status.OK if ok else Status.FAIL,
                (have or "(global workspace folder)") + ("" if ok else f", want {want.worktree_base_path}"),
                ""
                if ok
                else f"run `workspace orca sync --apply` to set the worktree location to {want.worktree_base_path}",
            )
        )
    if want.color is not None:
        ok = repo.color.lower() == want.color.lower()
        findings.append(
            OrcaFinding(
                f"repo {name}",
                "badge color",
                Status.OK if ok else Status.FAIL,
                (repo.color or "(none)") + ("" if ok else f", want {want.color}"),
                "" if ok else f"run `workspace orca sync --apply` to set badge color to {want.color}",
            )
        )
    if want.icon is not None and not icon_renders(want.icon):
        findings.append(
            OrcaFinding(
                f"repo {name}",
                "icon",
                Status.FAIL,
                f"{want.icon} is not an icon Orca draws; it shows a folder",
                "use an emoji: icon, an image URL, or one of " + ", ".join(sorted(ORCA_LUCIDE_ICONS)),
            )
        )
    elif want.icon is not None:
        ok = icon_matches(want.icon, repo)
        summary = repo.icon_identifier or "(none)"
        findings.append(
            OrcaFinding(
                f"repo {name}",
                "icon",
                Status.OK if ok else Status.FAIL,
                summary + ("" if ok else f", want {want.icon}"),
                "" if ok else f"run `workspace orca sync --apply` to set repo icon to {want.icon}",
            )
        )
    return findings


# ---------------------------------------------------------------- sync


@dataclass(slots=True, frozen=True)
class SyncAction:
    repo: str
    action: str  # "register", "update", "manual", "error"
    field: str
    old_value: str
    new_value: str
    applied: bool
    detail: str = ""


def settings_manual_actions(policy: OrcaPolicy, snapshot: OrcaSnapshot) -> list[SyncAction]:
    """Agent sleep, as a `manual` row: `settings.update` rejects its keys, and Orca's database must
    not be edited while it runs, so sync names the change to make in Orca's Settings instead.
    """
    if policy.agent_sleep is None or snapshot.settings is None:
        return []
    settings = snapshot.settings.settings
    check = agent_sleep_check(policy.agent_sleep, settings.agent_sleep, settings.agent_sleep_idle_ms)
    if check.ok:
        return []
    return [SyncAction("settings", "manual", "agent sleep", check.have, check.want, False, check.fix)]


@dataclass(slots=True, frozen=True)
class _FieldDiff:
    field: str
    old_value: str
    new_value: str


_SOCKET_FAILURE = "Orca's socket refused or was unreachable; these settings need Orca running"


def sync_repos(
    policy: OrcaPolicy,
    snapshot: OrcaSnapshot,
    *,
    estate_root: Path,
    apply: bool = False,
    target_repo: str | None = None,
    run: CommandRunner | None = None,
    rpc: RpcCaller | None = None,
    user_data: Path | None = None,
    retired: frozenset[str] = frozenset(),
) -> list[SyncAction]:
    """Plan, and with ``apply`` make, the changes that bring Orca's repos in line with the policy.

    Only fields that differ are sent. Scripts are never written to Orca from the policy: they
    belong in each repo's committed `orca.yaml`, so a missing one is reported as manual work.
    ``user_data`` picks the Orca instance (its user-data folder); None is the default instance.
    A ``retired`` project is never registered, even when the policy still names it: `estate orca`
    removes it daily, and registering it again would undo that every day.
    """
    if snapshot.repos is None:
        return [
            SyncAction("all", "error", "repos", "", "", False, "Orca repos could not be listed (run `orca open` first)")
        ]
    by_name = {repo.display_name: repo for repo in snapshot.repos}
    meta = read_orca_runtime_metadata(orca_runtime_metadata_path(user_data))
    auth_token = meta.auth_token if meta else ""
    run = run or orca_runner(user_data)
    rpc = rpc or bound_rpc(meta)
    actions: list[SyncAction] = []
    for name, want in sorted(policy.repos.items()):
        if target_repo is not None and name != target_repo:
            continue
        repo = by_name.get(name)
        if repo is None:
            if name in retired:
                actions.append(
                    SyncAction(
                        name, "manual", "registered", "unregistered", "", False, "retired; drop it from orca.json"
                    )
                )
                continue
            path = policy_repo_path(want.path) if want.path else estate_root / name
            actions.append(_register(name, path, apply=apply, run=run))
            continue
        context = _SyncContext(name, repo, apply=apply, run=run, rpc=rpc, auth_token=auth_token)
        actions += _sync_scripts(context, want)
        actions += _sync_cli_fields(context, want)
        actions += _sync_badge(context, want)
    return actions


@dataclass(slots=True, frozen=True)
class _SyncContext:
    name: str
    repo: OrcaRepo
    apply: bool
    run: CommandRunner
    rpc: RpcCaller | None
    auth_token: str

    def outcome(self, diff: _FieldDiff, ok: bool, failure: str) -> SyncAction:
        if not self.apply:
            return SyncAction(self.name, "update", diff.field, diff.old_value, diff.new_value, False, "planned")
        action = "update" if ok else "error"
        return SyncAction(self.name, action, diff.field, diff.old_value, diff.new_value, ok, "" if ok else failure)


def _register(name: str, path: Path, *, apply: bool, run: CommandRunner) -> SyncAction:
    target = path.as_posix()
    if not path.exists():
        return SyncAction(name, "error", "registered", "unregistered", target, False, f"{target} does not exist")
    if not apply:
        return SyncAction(name, "register", "registered", "unregistered", target, False, f"would add {target}")
    if run(["repo", "add", "--path", target]) is None:
        return SyncAction(name, "error", "registered", "unregistered", target, False, "`orca repo add` failed")
    return SyncAction(
        name, "register", "registered", "unregistered", target, True, "added; run sync again for its settings"
    )


def _sync_scripts(ctx: _SyncContext, want: RepoPolicy) -> list[SyncAction]:
    """Clear local scripts that shadow a matching `orca.yaml`; report scripts `orca.yaml` lacks."""
    local = ctx.repo.hook_settings
    shared = read_orca_yaml(Path(ctx.repo.path))
    manual: list[SyncAction] = []
    shadowed: list[_FieldDiff] = []
    for wanted in _wanted_scripts(want):
        source = resolve(wanted.kind, local, shared)
        field_name = f"{wanted.kind} script"
        if source.shared == wanted.script:
            if source.policy == "local-only" or source.effective != wanted.script:
                shadowed.append(_FieldDiff(field_name, source.local or "(none)", f"{ORCA_YAML}: {wanted.script}"))
        elif source.effective != wanted.script:
            detail = f"commit {ORCA_YAML} to {ctx.name} declaring scripts.{wanted.kind}; sync does not write repo files"
            manual.append(
                SyncAction(ctx.name, "manual", field_name, source.effective or "(none)", wanted.script, False, detail)
            )
    if not shadowed:
        return manual
    # Clearing drops every local script, so it is safe only when orca.yaml covers each one.
    kinds: list[ScriptKind] = ["setup", "archive"]
    sources = [resolve(kind, local, shared) for kind in kinds]
    orphaned = [kind for kind, source in zip(kinds, sources, strict=True) if source.local and not source.shared]
    if orphaned:
        detail = f"local {', '.join(orphaned)} script has no {ORCA_YAML} equivalent; clearing would lose it"
        return manual + [
            SyncAction(ctx.name, "manual", d.field, d.old_value, d.new_value, False, detail) for d in shadowed
        ]
    ok = ctx.apply and update_repo(
        ctx.repo.path,
        RepoRpcUpdates(hook_settings=defer_to_orca_yaml(local)),
        auth_token=ctx.auth_token,
        rpc=ctx.rpc,
    )
    return manual + [ctx.outcome(diff, ok, _SOCKET_FAILURE) for diff in shadowed]


def _sync_cli_fields(ctx: _SyncContext, want: RepoPolicy) -> list[SyncAction]:
    """Visibility and base ref, through the documented `orca repo` commands."""
    actions: list[SyncAction] = []
    path = ctx.repo.path
    visibility = want.external_worktree_visibility
    have_visibility = ctx.repo.external_worktree_visibility or "inherit"
    if visibility is not None and have_visibility != visibility:
        ok = ctx.apply and set_visibility(path, visibility, ctx.run)
        diff = _FieldDiff("external worktrees", have_visibility, visibility)
        actions.append(ctx.outcome(diff, ok, "`orca repo set` failed"))
    if want.base_ref is not None and ctx.repo.base_ref != want.base_ref:
        ok = ctx.apply and set_base_ref(path, want.base_ref, ctx.run)
        diff = _FieldDiff("base ref", ctx.repo.base_ref or "(none)", want.base_ref)
        actions.append(ctx.outcome(diff, ok, "`orca repo set-base-ref` failed"))
    return actions


def _sync_badge(ctx: _SyncContext, want: RepoPolicy) -> list[SyncAction]:
    """Fields only `repo.update` can set (colour, icon, worktree location), in one call carrying just the changes."""
    color = want.color if want.color is not None and ctx.repo.color.lower() != want.color.lower() else None
    icon = want.icon if want.icon is not None and not icon_matches(want.icon, ctx.repo) else None
    have_base = (ctx.repo.worktree_base_path or "").strip()
    want_base = want.worktree_base_path.strip() if want.worktree_base_path is not None else None
    base_path = want_base if want_base is not None and want_base != have_base else None
    diffs: list[_FieldDiff] = []
    if color is not None:
        diffs.append(_FieldDiff("badge color", ctx.repo.color or "(none)", color))
    if icon is not None:
        diffs.append(_FieldDiff("icon", ctx.repo.icon_identifier or "(none)", icon))
    if base_path is not None:
        diffs.append(_FieldDiff("worktree location", have_base or "(global)", base_path or "(global)"))
    if not diffs:
        return []
    updates = RepoRpcUpdates(
        badge_color=color,
        repo_icon=build_repo_icon_payload(icon) if icon else None,
        worktree_base_path=base_path,
    )
    ok = ctx.apply and update_repo(ctx.repo.path, updates, auth_token=ctx.auth_token, rpc=ctx.rpc)
    return [ctx.outcome(diff, ok, _SOCKET_FAILURE) for diff in diffs]
