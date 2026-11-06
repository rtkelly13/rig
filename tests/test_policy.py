"""Tests for estate-wide command policy evaluation."""

import json
from pathlib import Path

import pytest

from rig.policy.engine import audit_project_commands, check_command, load_policy, summarize_history
from rig.policy.models import CommandRisk, ProjectionStatus
from rig.policy.projections import project_native_permissions
from rig.typed_json import decode, decode_json


@pytest.mark.ci
def test_policy_classifies_validation_and_dangerous_commands():
    policy = load_policy()

    assert check_command("uv run pytest tests/test_policy.py", policy) is CommandRisk.SAFE
    assert check_command("pnpm build", policy) is CommandRisk.LOCAL
    assert check_command("pnpm test:watch", policy) is CommandRisk.LOCAL
    assert check_command("pnpm publish", policy) is CommandRisk.EXTERNAL
    assert check_command("git push origin main", policy) is CommandRisk.SAFE
    assert check_command("git fetch --all", policy) is CommandRisk.SAFE
    assert check_command("git branch -a", policy) is CommandRisk.SAFE
    assert check_command("git show HEAD", policy) is CommandRisk.SAFE
    assert check_command("git rev-parse --show-toplevel", policy) is CommandRisk.SAFE
    assert check_command("git worktree list", policy) is CommandRisk.SAFE
    assert check_command("git reset --hard HEAD", policy) is CommandRisk.DENIED
    assert check_command("git branch -D old-branch", policy) is CommandRisk.DENIED
    assert check_command("unknown-tool do-something", policy) is CommandRisk.DENIED


@pytest.mark.ci
def test_audit_project_commands_reads_python_and_node_manifests(tmp_path: Path):
    (tmp_path / "package.json").write_text('{"scripts":{"test":"vitest","build":"next build"}}', encoding="utf-8")
    (tmp_path / "pyproject.toml").write_text(
        '[project.scripts]\ncheck = "repo_health.cli:main"\n',
        encoding="utf-8",
    )

    commands = audit_project_commands(tmp_path)

    assert {(item.invocation, item.risk) for item in commands} == {
        ("pnpm test", CommandRisk.SAFE),
        ("pnpm build", CommandRisk.LOCAL),
        ("uv run check", CommandRisk.SAFE),
    }


@pytest.mark.ci
def test_history_summary_does_not_expose_raw_commands(tmp_path: Path):
    history = tmp_path / ".zsh_history"
    history.write_text(
        ": 1:0;uv run pytest tests/test_x.py --token=secret\n: 2:0;pnpm typecheck\n",
        encoding="utf-8",
    )

    counts = summarize_history(history)

    assert counts == {"uv validation": 1, "pnpm validation": 1}


def _write_policy(tmp_path: Path, policy: object) -> Path:
    path = tmp_path / "policy.json"
    path.write_text(json.dumps(policy), encoding="utf-8")
    return path


@pytest.mark.ci
def test_policy_file_accepts_bare_and_annotated_rules(tmp_path: Path):
    path = _write_policy(
        tmp_path,
        {
            "skills": {"allow": ["one"]},
            "commands": {"safe": ["uv run help", {"pattern": "uv run pytest*", "rationale": "Tests."}]},
        },
    )

    policy = load_policy(path)

    assert policy.skills_allowlist == ["one"]
    assert [(rule.pattern, rule.rationale) for rule in policy.command_rules] == [
        ("uv run help", ""),
        ("uv run pytest*", "Tests."),
    ]


@pytest.mark.ci
@pytest.mark.parametrize(
    "policy",
    [
        pytest.param({"commands": {"denied": [{"rationale": "No pattern."}]}}, id="rule-without-pattern"),
        pytest.param({"commands": {"denied": "git push*"}}, id="tier-not-a-list"),
        pytest.param({"commands": {"forbidden": ["git push*"]}}, id="unknown-tier"),
        pytest.param({"skills": {"allow": []}}, id="no-commands"),
    ],
)
def test_policy_file_fails_closed_on_a_misshapen_rule(tmp_path: Path, policy: object):
    """A dropped deny rule would let a broader allow rule match, so a bad rule is an error."""
    with pytest.raises(ValueError):
        load_policy(_write_policy(tmp_path, policy))


@pytest.mark.ci
def test_native_projection_reports_a_misshapen_permissions_block(tmp_path: Path):
    home = tmp_path / "home"
    claude = home / ".claude" / "settings.json"
    claude.parent.mkdir(parents=True)
    original = '{"permissions":{"allow":"Bash(git:*)"}}'
    claude.write_text(original, encoding="utf-8")

    (result,) = project_native_permissions(load_policy(), systems=["claude"], home=home)

    assert result.status is ProjectionStatus.FAILED
    assert result.detail == "permissions.allow must be an array"
    assert claude.read_text(encoding="utf-8") == original


@pytest.mark.ci
def test_native_projections_preserve_user_configuration_and_are_idempotent(tmp_path: Path):
    home = tmp_path / "home"
    claude = home / ".claude" / "settings.json"
    claude.parent.mkdir(parents=True)
    claude.write_text('{"model":"custom","permissions":{"allow":["Bash(custom *)"]}}\n', encoding="utf-8")
    opencode = home / ".config" / "opencode" / "opencode.json"
    opencode.parent.mkdir(parents=True)
    opencode.write_text('{"provider":{"custom":{"api":"keep"}}}\n', encoding="utf-8")
    codex = home / ".codex" / "rules" / "default.rules"
    codex.parent.mkdir(parents=True)
    codex.write_text('prefix_rule(pattern=["custom"], decision="allow")\n', encoding="utf-8")
    antigravity = home / ".gemini" / "antigravity-cli" / "settings.json"
    antigravity.parent.mkdir(parents=True)
    antigravity.write_text('{"theme":"dark","permissions":{"allow":["command(custom)"]}}\n', encoding="utf-8")

    first = project_native_permissions(load_policy(), home=home)

    assert [item.status for item in first] == [
        ProjectionStatus.APPLIED,
        ProjectionStatus.APPLIED,
        ProjectionStatus.APPLIED,
        ProjectionStatus.APPLIED,
    ]
    assert '"model": "custom"' in claude.read_text(encoding="utf-8")
    assert "Bash(uv run pytest:*)" in claude.read_text(encoding="utf-8")
    assert '"api": "keep"' in opencode.read_text(encoding="utf-8")
    opencode_content = opencode.read_text(encoding="utf-8")
    assert '"shell"' in opencode_content
    assert '"bash"' in opencode_content
    assert '"external_directory"' in opencode_content
    assert '"~/code/**": "allow"' in opencode_content
    assert '"uv run pytest *": "allow"' in opencode_content
    assert not (opencode.parent / "plugins" / "estate-guard.ts").exists()
    assert 'pattern=["custom"]' in codex.read_text(encoding="utf-8")
    assert "BEGIN shared-utilities policy" in codex.read_text(encoding="utf-8")
    assert '"theme": "dark"' in antigravity.read_text(encoding="utf-8")
    assert "command(custom)" in antigravity.read_text(encoding="utf-8")
    assert "command(git push)" in antigravity.read_text(encoding="utf-8")
    assert "command(git fetch)" in antigravity.read_text(encoding="utf-8")
    assert "command(git reset --hard)" in antigravity.read_text(encoding="utf-8")
    assert "unsandboxed" not in antigravity.read_text(encoding="utf-8")

    second = project_native_permissions(load_policy(), home=home)

    assert [item.status for item in second] == [
        ProjectionStatus.UNCHANGED,
        ProjectionStatus.UNCHANGED,
        ProjectionStatus.UNCHANGED,
        ProjectionStatus.UNCHANGED,
    ]


@pytest.mark.ci
def test_antigravity_projection_migrates_legacy_unsandboxed_rules(tmp_path: Path):
    home = tmp_path / "home"
    antigravity = home / ".gemini" / "antigravity-cli" / "settings.json"
    antigravity.parent.mkdir(parents=True)
    antigravity.write_text(
        json.dumps(
            {
                "permissions": {
                    "allow": ["command(custom)", "unsandboxed(dotnet)", "unsandboxed(git push)"],
                    "deny": ["unsandboxed(git reset --hard)"],
                }
            }
        )
        + "\n",
        encoding="utf-8",
    )

    results = project_native_permissions(load_policy(), systems=["antigravity"], home=home)

    assert results[0].status is ProjectionStatus.APPLIED
    content = antigravity.read_text(encoding="utf-8")
    assert "unsandboxed" not in content
    assert "command(dotnet)" in content
    assert "command(git push)" in content
    assert "command(git reset --hard)" in content

    settings = decode_json(dict[str, object], content)
    permissions = decode(dict[str, object], settings.get("permissions"))
    allow = decode(list[object], permissions.get("allow"))
    deny = decode(list[object], permissions.get("deny"))
    assert allow.count("command(git push)") == 1
    assert allow.count("command(dotnet)") == 1
    assert "command(custom)" in allow
    assert deny.count("command(git reset --hard)") == 1


@pytest.mark.ci
def test_composed_shell_commands_are_denied_before_policy_matching():
    policy = load_policy()

    assert check_command("uv run pytest tests && git push", policy) is CommandRisk.DENIED
    assert check_command("uv run pytest $(printf hidden)", policy) is CommandRisk.DENIED


@pytest.mark.ci
def test_mutating_flags_are_denied_even_under_read_only_wildcards():
    policy = load_policy()

    mutating = (
        "find . -delete",
        "find -delete .",
        "find . -name tmp -delete",
        "find . -exec rm scratch {}",
        "find . -execdir sh {} +",
        "find -name scratch -exec cat {} +",
        "find . -ok rm scratch {}",
        "sort -o out.txt in.txt",
        "sort in.txt -o out.txt",
        "git reflog expire --expire=now --all",
        "git reflog delete HEAD@{0}",
        "agent-browser eval document.cookie",
        "agent-browser run --session s eval window.fetch",
        "agent-browser connect 9222",
        "agent-browser run connect https://attacker.example",
    )
    for command in mutating:
        assert check_command(command, policy) is CommandRisk.DENIED, command


@pytest.mark.ci
def test_read_only_families_stay_safe_after_hardening():
    policy = load_policy()

    assert check_command("find . -name policy.py", policy) is CommandRisk.SAFE
    assert check_command("find . -maxdepth 2 -type d", policy) is CommandRisk.SAFE
    assert check_command("sort in.txt", policy) is CommandRisk.SAFE
    assert check_command("git reflog show HEAD", policy) is CommandRisk.SAFE
    assert check_command("git reflog HEAD", policy) is CommandRisk.SAFE
    assert check_command("agent-browser run open https://example.com", policy) is CommandRisk.SAFE
    assert check_command("agent-browser run click @e1", policy) is CommandRisk.SAFE
    assert check_command("agent-browser run snapshot -i", policy) is CommandRisk.SAFE


@pytest.mark.ci
def test_pnpm_filter_deny_is_excluded_from_claude_but_projected_to_antigravity(tmp_path: Path):
    home = tmp_path / "home"
    claude = home / ".claude" / "settings.json"
    claude.parent.mkdir(parents=True)
    claude.write_text(
        '{"permissions":{"deny":["Bash(pnpm --filter:*)","Bash(pnpm -F:*)","Bash(custom)"]}}\n', encoding="utf-8"
    )

    project_native_permissions(load_policy(), systems=["claude", "antigravity"], home=home)

    claude_deny = decode(
        list[object],
        decode(
            dict[str, object], decode_json(dict[str, object], claude.read_text(encoding="utf-8")).get("permissions")
        ).get("deny"),
    )
    assert "Bash(pnpm --filter:*)" not in claude_deny
    assert "Bash(pnpm -F:*)" not in claude_deny
    assert "Bash(pnpm --dir:*)" in claude_deny
    assert "Bash(git -C:*)" in claude_deny
    assert "Bash(custom)" in claude_deny
    antigravity = (home / ".gemini" / "antigravity-cli" / "settings.json").read_text(encoding="utf-8")
    assert "command(pnpm --filter)" in antigravity
    assert "command(pnpm -F)" in antigravity


@pytest.mark.ci
def test_merge_requested_gh_commands_are_safe_but_admin_merges_are_denied():
    policy = load_policy()

    for command in (
        "gh pr ready 42",
        "gh pr merge 42 --auto --squash",
        "gh pr merge 42 --disable-auto",
        "gh stack merge 42 --yes",
        "gh run rerun 1234567890",
    ):
        assert check_command(command, policy) is CommandRisk.SAFE, command
    for command in (
        "gh pr merge 42 --admin --auto --squash",
        "gh pr merge --admin 42",
        "gh pr merge 42 --squash --admin",
        "gh pr merge 42 --squash",
        "gh stack merge 42",
    ):
        assert check_command(command, policy) is not CommandRisk.SAFE, command
    assert check_command("gh pr merge 42 --admin --auto --squash", policy) is CommandRisk.DENIED


@pytest.mark.ci
def test_claude_projection_keeps_interior_wildcards_and_denies_admin_merges(tmp_path: Path):
    home = tmp_path / "home"

    (result,) = project_native_permissions(load_policy(), systems=["claude"], home=home)

    assert result.status is ProjectionStatus.APPLIED
    settings = decode_json(dict[str, object], (home / ".claude" / "settings.json").read_text(encoding="utf-8"))
    permissions = decode(dict[str, object], settings.get("permissions"))
    allow = decode(list[object], permissions.get("allow"))
    deny = decode(list[object], permissions.get("deny"))
    assert "Bash(gh pr ready:*)" in allow
    assert "Bash(gh pr merge * --auto --squash)" in allow
    assert "Bash(gh pr merge * --disable-auto)" in allow
    assert "Bash(gh stack merge * --yes)" in allow
    assert "Bash(gh run rerun:*)" in allow
    assert "Bash(gh pr merge *--admin*)" in deny
    assert not any(isinstance(rule, str) and rule.startswith("Bash(gh pr merge:") for rule in allow)
