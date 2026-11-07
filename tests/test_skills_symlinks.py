"""Unit tests for multi-agent AI skills discovery, auditing, and symlink synchronization."""

from pathlib import Path

import pytest
from typer.testing import CliRunner

from rig.fs_links import is_link
from rig.skills.engine import (
    audit_skill_symlinks,
    discover_all_skills,
    get_system_directory,
    prune_broken_skill_symlinks,
    sync_skill_symlinks,
)
from rig.skills.models import CliSystem
from rig.workspace.cli import app
from tests.links import dangling_dir_link, dir_link

runner = CliRunner()


@pytest.fixture
def mock_skills_env(tmp_path: Path):
    """Sets up a temporary skill sources structure and simulated agent home directory."""
    # 1. Source skills directory
    sources_dir = tmp_path / "sources"
    sources_dir.mkdir()

    # Skill 1
    skill1 = sources_dir / "engineering" / "tdd"
    skill1.mkdir(parents=True)
    (skill1 / "SKILL.md").write_text(
        "---\nname: tdd\ndescription: Test-driven development workflow\n---\n# TDD Docs\n",
        encoding="utf-8",
    )

    # Skill 2
    skill2 = sources_dir / "productivity" / "disk-cleanup"
    skill2.mkdir(parents=True)
    (skill2 / "SKILL.md").write_text(
        "---\nname: disk-cleanup\ndescription: Storage triage and cleanup runbook\n---\n# Cleanup\n",
        encoding="utf-8",
    )

    # Ignored directory with SKILL.md
    ignored = sources_dir / "node_modules" / "some-pkg"
    ignored.mkdir(parents=True)
    (ignored / "SKILL.md").write_text("---\nname: bad-pkg\n---\n", encoding="utf-8")

    # 2. Simulated home directory
    fake_home = tmp_path / "home"
    fake_home.mkdir()

    return sources_dir, fake_home


@pytest.mark.ci
def test_discover_skills_priority_and_filtering(mock_skills_env):
    sources_dir, _ = mock_skills_env

    skills = discover_all_skills(search_paths=[sources_dir])
    names = [s.name for s in skills]

    assert "tdd" in names
    assert "disk-cleanup" in names
    assert "bad-pkg" not in names  # Ignored node_modules

    tdd_manifest = next(s for s in skills if s.name == "tdd")
    assert tdd_manifest.description == "Test-driven development workflow"


@pytest.mark.ci
def test_estate_allowlist_is_applied_by_default(tmp_path: Path):
    sources_dir = tmp_path / "skills"
    for name in ("kept", "excluded"):
        folder = sources_dir / name
        folder.mkdir(parents=True)
        (folder / "SKILL.md").write_text(
            f"---\nname: {name}\ndescription: {name}\n---\n",
            encoding="utf-8",
        )

    allowlist = tmp_path / "skills-allowlist.json"
    allowlist.write_text('["kept"]', encoding="utf-8")

    from rig.skills import engine

    discovered = discover_all_skills(
        search_paths=[sources_dir],
        allowlist=engine.load_estate_allowlist(allowlist),
        use_estate_allowlist=False,
    )

    assert [skill.name for skill in discovered] == ["kept"]


@pytest.mark.ci
def test_sync_and_audit_symlinks(mock_skills_env):
    sources_dir, fake_home = mock_skills_env
    systems = [CliSystem.OPENCODE, CliSystem.CLAUDE, CliSystem.CODEX, CliSystem.ANTIGRAVITY]

    # 1. Audit before sync -> all missing
    report_before = audit_skill_symlinks(systems=systems, home=fake_home, search_paths=[sources_dir])
    assert not report_before.is_clean
    for sys in systems:
        status = report_before.systems[sys]
        assert not status.dir_exists
        assert len(status.missing_skills) == 2

    # 2. Dry run -> should not create files
    dry_results = sync_skill_symlinks(systems=systems, dry_run=True, home=fake_home, search_paths=[sources_dir])
    assert dry_results[CliSystem.OPENCODE] == 2
    assert not (fake_home / ".claude" / "skills").exists()

    # 3. Real sync -> creates directories and symlinks
    sync_results = sync_skill_symlinks(systems=systems, dry_run=False, home=fake_home, search_paths=[sources_dir])
    assert all(count == 2 for count in sync_results.values())

    # 4. Audit after sync -> all healthy
    report_after = audit_skill_symlinks(systems=systems, home=fake_home, search_paths=[sources_dir])
    assert report_after.is_clean
    for sys in systems:
        status = report_after.systems[sys]
        assert status.is_healthy
        assert len(status.valid_links) == 2
        assert len(status.broken_links) == 0

        # Verify actual symlink target
        sys_dir = get_system_directory(sys, home=fake_home)
        link = sys_dir / "tdd"
        assert is_link(link)
        assert (link / "SKILL.md").is_file()


@pytest.mark.ci
def test_prune_broken_and_stale_symlinks(mock_skills_env):
    sources_dir, fake_home = mock_skills_env
    systems = [CliSystem.CLAUDE]

    # Sync first
    sync_skill_symlinks(systems=systems, dry_run=False, home=fake_home, search_paths=[sources_dir])
    claude_dir = get_system_directory(CliSystem.CLAUDE, home=fake_home)

    # Inject a broken symlink (points to nonexistent path)
    broken_link = claude_dir / "broken-skill"
    dangling_dir_link(broken_link, sources_dir / "nonexistent-target")

    # Inject a stale valid symlink (points to valid dir, but not in current catalog)
    stale_target = fake_home / "some-external-folder"
    stale_target.mkdir()
    (stale_target / "SKILL.md").write_text("# Other", encoding="utf-8")
    stale_link = claude_dir / "stale-skill"
    dir_link(stale_link, stale_target)

    # Audit detects both broken and stale
    report = audit_skill_symlinks(systems=systems, home=fake_home, search_paths=[sources_dir])
    status = report.systems[CliSystem.CLAUDE]
    assert "broken-skill" in status.broken_links
    assert "stale-skill" in status.stale_links

    # Prune broken links specifically
    pruned_broken = prune_broken_skill_symlinks(systems=systems, home=fake_home)
    assert pruned_broken[CliSystem.CLAUDE] == 1
    assert not broken_link.exists()
    assert stale_link.exists()  # Stale valid link kept by prune_broken

    # Full sync with prune removes stale links
    sync_skill_symlinks(systems=systems, prune=True, home=fake_home, search_paths=[sources_dir])
    assert not stale_link.exists()
    assert (claude_dir / "tdd").exists()


@pytest.mark.ci
def test_workspace_skills_cli():
    # 1. Test status command
    res_status = runner.invoke(app, ["skills", "status"])
    assert res_status.exit_code == 0
    assert "AI Agent Skills Symlink Verification" in res_status.output

    res_targeted = runner.invoke(app, ["skills", "sync", "--only", "skills-sync", "--dry-run", "-s", "claude"])
    assert res_targeted.exit_code == 0
    assert "reversible skill overlay" in res_targeted.output

    res_all = runner.invoke(app, ["skills", "sync", "--all", "--dry-run", "-s", "claude"])
    assert res_all.exit_code == 0
    assert "all discovered" in res_all.output

    # 2. Test list command
    res_list = runner.invoke(app, ["skills", "list"])
    assert res_list.exit_code == 0
    assert "Skills Catalog" in res_list.output

    # 3. Test sync dry-run
    res_sync = runner.invoke(app, ["skills", "sync", "--dry-run", "-s", "claude"])
    assert res_sync.exit_code == 0
    assert "Would link" in res_sync.output

    # 4. Test invalid system validation
    res_invalid = runner.invoke(app, ["skills", "status", "-s", "unknown-tool"])
    assert res_invalid.exit_code == 1
    assert "Unknown system" in res_invalid.output


@pytest.mark.ci
def test_extract_vendor_skills(tmp_path: Path):
    from rig.skills.vendor import UpstreamVendor, extract_vendor_skills

    fake_repo = tmp_path / "mock-repo"
    skill1 = fake_repo / "skills" / "awesome-skill"
    skill1.mkdir(parents=True)
    (skill1 / "SKILL.md").write_text("---\nname: awesome-skill\n---\n", encoding="utf-8")
    (skill1 / "helper.py").write_text("print('hello')", encoding="utf-8")

    vendor = UpstreamVendor(name="test-vendor", url="https://example.com/skills.git", skills_subdir="skills")
    dest_vendored = tmp_path / "dest-vendored"

    extracted = extract_vendor_skills(vendor, fake_repo, target_vendored_dir=dest_vendored)
    assert extracted == ["awesome-skill"]

    installed_skill = dest_vendored / "test-vendor" / "awesome-skill"
    assert installed_skill.is_dir()
    assert (installed_skill / "SKILL.md").is_file()
    assert (installed_skill / "helper.py").is_file()


@pytest.mark.ci
def test_upstream_vendor_filter_rules():
    from rig.skills.vendor import UpstreamVendor

    # Wildcard vendor
    v_all = UpstreamVendor(name="v1", url="http://example.com", include=("*",))
    assert v_all.is_skill_included("any-skill")
    assert v_all.is_skill_included("another-one")

    # Explicit whitelist
    v_pick = UpstreamVendor(
        name="v2",
        url="http://example.com",
        include=("subagent-*", "systematic-debugging"),
    )
    assert v_pick.is_skill_included("subagent-driven-development")
    assert v_pick.is_skill_included("systematic-debugging")
    assert not v_pick.is_skill_included("brainstorming")

    # Exclusion override
    v_exc = UpstreamVendor(
        name="v3",
        url="http://example.com",
        include=("*",),
        exclude=("legacy-*", "bad-skill"),
    )
    assert v_exc.is_skill_included("good-skill")
    assert not v_exc.is_skill_included("legacy-old")
    assert not v_exc.is_skill_included("bad-skill")


@pytest.mark.ci
def test_extract_vendor_skills_with_filtering_and_cleanup(tmp_path: Path):
    from rig.skills.vendor import UpstreamVendor, extract_vendor_skills

    fake_repo = tmp_path / "mock-repo"
    # Create 3 skills in repo
    for name in ["skill-a", "skill-b", "skill-c"]:
        folder = fake_repo / "skills" / name
        folder.mkdir(parents=True)
        (folder / "SKILL.md").write_text(f"---\nname: {name}\n---\n", encoding="utf-8")

    dest_vendored = tmp_path / "dest-vendored"

    # 1. Extract only skill-a and skill-b
    v = UpstreamVendor(name="repo-x", url="http://x", include=["skill-a", "skill-b"])
    extracted = extract_vendor_skills(v, fake_repo, target_vendored_dir=dest_vendored)
    assert set(extracted) == {"skill-a", "skill-b"}
    assert (dest_vendored / "repo-x" / "skill-a").is_dir()
    assert (dest_vendored / "repo-x" / "skill-b").is_dir()
    assert not (dest_vendored / "repo-x" / "skill-c").exists()

    # 2. Update filter to only skill-b (skill-a should be purged)
    v2 = UpstreamVendor(name="repo-x", url="http://x", include=["skill-b"])
    extracted2 = extract_vendor_skills(v2, fake_repo, target_vendored_dir=dest_vendored)
    assert extracted2 == ["skill-b"]
    assert (dest_vendored / "repo-x" / "skill-b").is_dir()
    assert not (dest_vendored / "repo-x" / "skill-a").exists()


@pytest.mark.ci
def test_vendor_config_schema_validation(tmp_path: Path):
    import json

    from pydantic import ValidationError

    from rig.skills.vendor import load_upstream_vendors

    cfg_file = tmp_path / "upstream-skills.json"

    # 1. Valid config schema
    valid_data = [
        {
            "name": "test1",
            "url": "https://example.com/repo1.git",
            "folder_name": "repo1",
            "branch": "main",
            "skills_subdir": "skills",
            "include": ["skill-a", "skill-b"],
            "exclude": ["skill-c"],
        }
    ]
    cfg_file.write_text(json.dumps(valid_data), encoding="utf-8")

    loaded = load_upstream_vendors(config_path=cfg_file)
    assert len(loaded) == 1
    assert loaded[0].name == "test1"
    assert loaded[0].include == ["skill-a", "skill-b"]
    assert loaded[0].exclude == ["skill-c"]

    # 2. Invalid config schema (missing required 'url' field)
    invalid_data = [{"name": "bad-vendor"}]
    cfg_file.write_text(json.dumps(invalid_data), encoding="utf-8")

    with pytest.raises(ValidationError):
        load_upstream_vendors(config_path=cfg_file)


@pytest.mark.ci
def test_upstream_vendor_allowlist_field():
    from rig.skills.vendor import UpstreamVendor

    # 1. Vendor using allowlist
    v_allow = UpstreamVendor(
        name="test-allow",
        url="https://example.com/repo.git",
        allowlist=["tdd", "codebase-design"],
    )
    assert v_allow.effective_allowlist == ["tdd", "codebase-design"]
    assert v_allow.is_skill_included("tdd")
    assert v_allow.is_skill_included("codebase-design")
    assert not v_allow.is_skill_included("ask-matt")

    # 2. Allowlist takes precedence over include
    v_both = UpstreamVendor(
        name="test-both",
        url="https://example.com/repo.git",
        allowlist=["tdd"],
        include=["*"],
    )
    assert v_both.effective_allowlist == ["tdd"]
    assert v_both.is_skill_included("tdd")
    assert not v_both.is_skill_included("any-other-skill")


@pytest.mark.ci
def test_estate_allowlist_loading_and_filtering(tmp_path: Path):
    import json

    from rig.skills.engine import (
        audit_skill_symlinks,
        discover_all_skills,
        load_estate_allowlist,
    )
    from rig.skills.models import CliSystem

    # 1. Setup mock skills
    sources_dir = tmp_path / "skills"
    for name in ["skill-one", "skill-two", "skill-three"]:
        folder = sources_dir / name
        folder.mkdir(parents=True)
        (folder / "SKILL.md").write_text(f"---\nname: {name}\n---\n", encoding="utf-8")

    # 2. Without allowlist -> 3 skills
    skills_all = discover_all_skills(search_paths=[sources_dir], use_estate_allowlist=False)
    assert len(skills_all) == 3

    # 3. With explicit allowlist -> 2 skills
    skills_filtered = discover_all_skills(
        search_paths=[sources_dir],
        allowlist=["skill-one", "skill-three"],
        use_estate_allowlist=False,
    )
    assert {s.name for s in skills_filtered} == {"skill-one", "skill-three"}

    # 4. With estate allowlist file
    allowlist_file = tmp_path / "skills-allowlist.json"
    allowlist_file.write_text(json.dumps(["skill-one"]), encoding="utf-8")

    loaded_allowlist = load_estate_allowlist(allowlist_path=allowlist_file)
    assert loaded_allowlist == ["skill-one"]

    # 5. Audit report marks allowlist_active
    report = audit_skill_symlinks(
        systems=[CliSystem.OPENCODE],
        home=tmp_path / "home",
        search_paths=[sources_dir],
        allowlist=loaded_allowlist,
        use_estate_allowlist=False,
    )
    assert report.allowlist_active is True
    assert len(report.discovered_skills) == 1
    assert report.discovered_skills[0].name == "skill-one"


@pytest.mark.ci
def test_skill_activation_overlay_is_reversible_and_idempotent(tmp_path: Path):
    from rig.skills.engine import activate_skills, deactivate_skills, load_skill_activation_state

    sources_dir = tmp_path / "skills"
    for name in ["curated", "hidden"]:
        folder = sources_dir / name
        folder.mkdir(parents=True)
        (folder / "SKILL.md").write_text(f"---\nname: {name}\n---\n", encoding="utf-8")
    home = tmp_path / "home"

    activate_skills(["hidden"], systems=[CliSystem.OPENCODE], home=home, search_paths=[sources_dir])
    activate_skills(["hidden"], systems=[CliSystem.OPENCODE], home=home, search_paths=[sources_dir])

    link = home / ".config" / "opencode" / "skills" / "hidden"
    assert is_link(link)
    assert load_skill_activation_state(home) == ["hidden"]

    deactivate_skills(["hidden"], systems=[CliSystem.OPENCODE], home=home, search_paths=[sources_dir])

    assert not link.exists()
    assert load_skill_activation_state(home) == []


@pytest.mark.ci
def test_deactivating_one_system_keeps_other_system_overlay_state(tmp_path: Path):
    from rig.skills.engine import activate_skills, deactivate_skills, load_skill_activation_state

    sources_dir = tmp_path / "skills"
    skill = sources_dir / "hidden"
    skill.mkdir(parents=True)
    (skill / "SKILL.md").write_text("---\nname: hidden\n---\n", encoding="utf-8")
    home = tmp_path / "home"
    systems = [CliSystem.OPENCODE, CliSystem.CLAUDE]

    activate_skills(["hidden"], systems=systems, home=home, search_paths=[sources_dir])
    deactivate_skills(["hidden"], systems=[CliSystem.OPENCODE], home=home, search_paths=[sources_dir])

    assert not (home / ".config" / "opencode" / "skills" / "hidden").exists()
    assert is_link(home / ".claude" / "skills" / "hidden")
    assert load_skill_activation_state(home) == ["hidden"]

    deactivate_skills(["hidden"], systems=[CliSystem.CLAUDE], home=home, search_paths=[sources_dir])
    assert load_skill_activation_state(home) == []


@pytest.mark.ci
def test_skill_activation_rejects_unknown_names(tmp_path: Path):
    from rig.skills.engine import activate_skills

    with pytest.raises(ValueError, match="Unknown skill"):
        activate_skills(["missing"], systems=[CliSystem.OPENCODE], home=tmp_path / "home", search_paths=[tmp_path])


@pytest.mark.ci
def test_prune_never_removes_agent_owned_directories(mock_skills_env):
    """A real directory in the target belongs to the CLI, not to this system.

    Claude Code keeps its own ``synced`` bucket in ~/.claude/skills; an earlier
    prune implementation rmtree'd any entry it did not recognise.
    """
    sources_dir, fake_home = mock_skills_env
    systems = [CliSystem.CLAUDE]

    sync_skill_symlinks(systems=systems, dry_run=False, home=fake_home, search_paths=[sources_dir])
    claude_dir = get_system_directory(CliSystem.CLAUDE, home=fake_home)

    # Agent-owned state: a real directory plus a real file, neither of them skills.
    synced = claude_dir / "synced"
    synced.mkdir()
    (synced / "bucket-abc123").write_text("agent state", encoding="utf-8")
    loose_file = claude_dir / "index.json"
    loose_file.write_text("{}", encoding="utf-8")

    sync_skill_symlinks(systems=systems, prune=True, home=fake_home, search_paths=[sources_dir])

    assert synced.is_dir() and not is_link(synced)
    assert (synced / "bucket-abc123").read_text(encoding="utf-8") == "agent state"
    assert loose_file.is_file()
    assert (claude_dir / "tdd").exists()


@pytest.mark.ci
def test_prune_still_removes_stale_and_dangling_links(mock_skills_env):
    """Guarding real entries must not weaken the mirror guarantee for links."""
    sources_dir, fake_home = mock_skills_env
    systems = [CliSystem.CLAUDE]

    sync_skill_symlinks(systems=systems, dry_run=False, home=fake_home, search_paths=[sources_dir])
    claude_dir = get_system_directory(CliSystem.CLAUDE, home=fake_home)

    dangling = claude_dir / "dangling-skill"
    dangling_dir_link(dangling, sources_dir / "gone")

    external = fake_home / "external-skill"
    external.mkdir()
    stale = claude_dir / "stale-skill"
    dir_link(stale, external)

    sync_skill_symlinks(systems=systems, prune=True, home=fake_home, search_paths=[sources_dir])

    assert not is_link(dangling)
    assert not is_link(stale)
    assert external.is_dir()  # only the link goes, never the target
    assert (claude_dir / "tdd").exists()


@pytest.mark.ci
def test_audit_ignores_agent_owned_entries_but_flags_shadowing(mock_skills_env):
    """Agent-owned state is not a collision; a real dir shadowing a skill is."""
    sources_dir, fake_home = mock_skills_env
    systems = [CliSystem.CLAUDE]

    sync_skill_symlinks(systems=systems, dry_run=False, home=fake_home, search_paths=[sources_dir])
    claude_dir = get_system_directory(CliSystem.CLAUDE, home=fake_home)

    (claude_dir / "synced").mkdir()

    report = audit_skill_symlinks(systems=systems, home=fake_home, search_paths=[sources_dir])
    status = report.systems[CliSystem.CLAUDE]
    assert status.collisions == []
    assert status.is_healthy

    # Now shadow a real catalog name with a directory: that IS a collision.
    (claude_dir / "tdd").unlink()
    (claude_dir / "tdd").mkdir()

    report = audit_skill_symlinks(systems=systems, home=fake_home, search_paths=[sources_dir])
    status = report.systems[CliSystem.CLAUDE]
    assert status.collisions == ["tdd"]
    assert not status.is_healthy
