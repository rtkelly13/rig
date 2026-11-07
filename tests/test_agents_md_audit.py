"""Tests for the `AGENTS.md` hygiene audit."""

from __future__ import annotations

import datetime as dt
import json
import pathlib
import subprocess

from rig.repo_governance.agents_md_audit import (
    WAIVER_FILENAME,
    Severity,
    Waiver,
    audit_repo,
    check_duplicate_headings,
    check_path_liveness,
    check_root_size,
    collect_metrics,
    strip_code,
)
from rig.repo_governance.policy_config import AgentsMdRules
from tests.links import file_symlink_or_skip

GOOD_ROOT = """# thing

A React component library for accessible data visualization.

This project uses pnpm.

## Commands

| | |
|---|---|
| `pnpm build` | builds it |
"""


def _repo(tmp_path: pathlib.Path, agents: str = GOOD_ROOT) -> pathlib.Path:
    (tmp_path / "AGENTS.md").write_text(agents, encoding="utf-8")
    return tmp_path


def test_clean_repo_has_no_findings(tmp_path: pathlib.Path) -> None:
    result = audit_repo(_repo(tmp_path), AgentsMdRules(claude_md="any"))
    assert result.findings == []
    assert not result.failed


def test_missing_agents_md_is_an_error(tmp_path: pathlib.Path) -> None:
    result = audit_repo(tmp_path, AgentsMdRules())
    assert result.failed
    assert [f.check for f in result.findings] == ["required-content"]


def test_root_size_budget() -> None:
    assert check_root_size("a\nb\nc", AgentsMdRules(max_lines=10)) == []
    findings = check_root_size("\n".join("x" * 200), AgentsMdRules(max_lines=10))
    assert findings[0].check == "root-size"
    assert findings[0].severity is Severity.ERROR


def test_duplicate_heading_reports_the_second_occurrence() -> None:
    text = "## Conventions\n\nfoo\n\n## Other\n\n## Conventions\n"
    findings = check_duplicate_headings(text)
    assert len(findings) == 1
    assert findings[0].line == 7
    assert "line 1" in findings[0].message


def test_path_liveness_needs_a_separator(tmp_path: pathlib.Path) -> None:
    """Bare suffixes are not paths — a naive check drowns in `.test.ts`."""
    (tmp_path / "src").mkdir()
    (tmp_path / "src" / "real.ts").write_text("", encoding="utf-8")
    text = "See `src/real.ts` and `src/gone.ts`, avoid `.test.ts` and `Prose.tsx`.\n"
    findings = check_path_liveness(tmp_path, text)
    assert [f.message.split("`")[1] for f in findings] == ["src/gone.ts"]


def test_pointer_integrity_catches_a_dead_link(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [docs](./docs/missing.md).\n")
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    assert [f.check for f in result.findings] == ["pointer-integrity"]
    assert result.failed


def test_orphan_doc_is_a_warning_and_a_linked_one_is_not(tmp_path: pathlib.Path) -> None:
    docs = tmp_path / "docs"
    docs.mkdir()
    (docs / "linked.md").write_text("# linked\n", encoding="utf-8")
    (docs / "orphan.md").write_text("# orphan\n", encoding="utf-8")
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [linked](./docs/linked.md).\n")
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    orphans = [f.path for f in result.findings if f.check == "docs-orphan"]
    assert orphans == ["docs/orphan.md"]
    assert not result.failed  # a warning, not an error


def test_readme_makes_a_directory_self_indexing(tmp_path: pathlib.Path) -> None:
    adr = tmp_path / "docs" / "adr"
    adr.mkdir(parents=True)
    (adr / "README.md").write_text("# index\n", encoding="utf-8")
    (adr / "0001-thing.md").write_text("# thing\n", encoding="utf-8")
    result = audit_repo(_repo(tmp_path), AgentsMdRules(claude_md="any"))
    assert [f.check for f in result.findings if f.check == "docs-orphan"] == []


def test_claude_md_copy_is_flagged_but_symlink_is_not(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    copy = repo / "CLAUDE.md"
    copy.write_text("# copy\n", encoding="utf-8")
    assert [f.check for f in audit_repo(repo, AgentsMdRules(claude_md="symlink")).findings] == ["claude-md-shape"]

    copy.unlink()
    file_symlink_or_skip(copy, "AGENTS.md")
    assert audit_repo(repo, AgentsMdRules(claude_md="symlink")).findings == []


def _git(repo: pathlib.Path, *args: str, stdin: str | None = None) -> str:
    return subprocess.run(
        ["git", "-C", str(repo), *args], input=stdin, capture_output=True, text=True, check=True
    ).stdout.strip()


def test_claude_md_symlink_checked_out_as_a_placeholder_file_is_not_flagged(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    _git(repo, "init", "-q")
    (repo / "CLAUDE.md").write_text("AGENTS.md", encoding="utf-8")
    blob = _git(repo, "hash-object", "-w", "--stdin", stdin="AGENTS.md")
    _git(repo, "update-index", "--add", "--cacheinfo", f"120000,{blob},CLAUDE.md")
    assert audit_repo(repo, AgentsMdRules(claude_md="symlink")).findings == []


def test_placeholder_edited_into_a_real_file_is_flagged(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    _git(repo, "init", "-q")
    blob = _git(repo, "hash-object", "-w", "--stdin", stdin="AGENTS.md")
    _git(repo, "update-index", "--add", "--cacheinfo", f"120000,{blob},CLAUDE.md")
    (repo / "CLAUDE.md").write_text("# Claude\n\nInstructions that drift from AGENTS.md\n", encoding="utf-8")
    assert [f.check for f in audit_repo(repo, AgentsMdRules(claude_md="symlink")).findings] == ["claude-md-shape"]


def test_committed_regular_file_naming_agents_md_is_flagged(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    _git(repo, "init", "-q")
    (repo / "CLAUDE.md").write_text("AGENTS.md", encoding="utf-8")
    _git(repo, "add", "CLAUDE.md")
    assert [f.check for f in audit_repo(repo, AgentsMdRules(claude_md="symlink")).findings] == ["claude-md-shape"]


def test_generation_marker_fails(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, "<!-- generated by init -->\n" + GOOD_ROOT)
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    assert "generation-marker" in [f.check for f in result.findings]
    assert result.failed


def test_required_content_reports_each_missing_piece(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, "# bare\n\n## Heading\n")
    result = audit_repo(repo, AgentsMdRules(claude_md="any", check_paths=False))
    missing = {f.message for f in result.findings if f.check == "required-content"}
    assert len(missing) == 3


# ---------------------------------------------------------------------------
# waivers — the dismissable, source-controlled part
# ---------------------------------------------------------------------------


def _write_waivers(repo: pathlib.Path, *waivers: Waiver) -> None:
    payload = {"waivers": [w.model_dump(mode="json") for w in waivers]}
    (repo / WAIVER_FILENAME).write_text(json.dumps(payload, indent=2), encoding="utf-8")


def test_a_reasoned_waiver_suppresses(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [gone](./docs/missing.md).\n")
    _write_waivers(repo, Waiver(check="pointer-integrity", path="AGENTS.md", reason="doc lands in #123"))
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    assert result.findings == []
    assert [f.check for f in result.suppressed] == ["pointer-integrity"]


def test_an_unreasoned_waiver_is_itself_an_error(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [gone](./docs/missing.md).\n")
    _write_waivers(repo, Waiver(check="pointer-integrity", path="AGENTS.md"))
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    checks = [f.check for f in result.findings]
    assert "waiver-unreasoned" in checks
    assert "pointer-integrity" in checks, "an unreasoned waiver must not suppress"
    assert result.failed


def test_an_expired_waiver_stops_suppressing(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [gone](./docs/missing.md).\n")
    _write_waivers(
        repo,
        Waiver(check="pointer-integrity", path="AGENTS.md", reason="temporary", expires="2020-01-01"),
    )
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    checks = [f.check for f in result.findings]
    assert "waiver-expired" in checks
    assert "pointer-integrity" in checks
    assert result.failed


def test_a_stale_waiver_is_reported_so_the_list_stays_honest(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    _write_waivers(repo, Waiver(check="root-size", path="AGENTS.md", reason="was long, now fixed"))
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    stale = [f for f in result.findings if f.check == "waiver-stale"]
    assert len(stale) == 1
    assert stale[0].severity is Severity.INFO
    assert not result.failed


def test_a_malformed_waiver_file_does_not_crash_the_audit(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    (repo / WAIVER_FILENAME).write_text("{ not json", encoding="utf-8")
    assert audit_repo(repo, AgentsMdRules(claude_md="any")).findings == []


def test_waiver_expiry_boundary() -> None:
    waiver = Waiver(check="x", path="y", reason="z", expires="2026-06-15")
    assert not waiver.expired_on(dt.date(2026, 6, 15))
    assert waiver.expired_on(dt.date(2026, 6, 16))
    assert Waiver(check="x", path="y", reason="z", expires="not-a-date").expired_on(dt.date(2026, 1, 1))


def test_metrics_are_collected_but_never_gated(tmp_path: pathlib.Path) -> None:
    text = "## A\n\n1. do this\n2. do that\n\nYou MUST and you NEVER.\n"
    metrics = collect_metrics(text)
    assert metrics["instruction-count"] == 2
    assert metrics["forcing-language"] == 2
    result = audit_repo(_repo(tmp_path), AgentsMdRules(claude_md="any"))
    assert set(result.metrics) == {"instruction-count", "forcing-language", "section-outlier"}
    assert not result.failed


def test_a_link_inside_code_is_an_example_not_a_pointer(tmp_path: pathlib.Path) -> None:
    """Documenting link syntax must not assert the example path exists."""
    body = GOOD_ROOT + "\nUse `![alt](./shot.png)` in the body.\n\n```\n[x](./also-fake.md)\n```\n"
    result = audit_repo(_repo(tmp_path, body), AgentsMdRules(claude_md="any", check_paths=False))
    assert [f.check for f in result.findings] == []


def test_strip_code_leaves_real_links_alone() -> None:
    text = "See [real](./docs/x.md) and `[fake](./y.md)`.\n"
    stripped = strip_code(text)
    assert "./docs/x.md" in stripped
    assert "./y.md" not in stripped


def _write(path: pathlib.Path, text: str = "# doc\n") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def test_small_docs_tree_needs_no_index(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [a](./docs/a.md).\n")
    _write(repo / "docs" / "a.md")
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    assert [f for f in result.findings if f.check == "docs-index"] == []


def test_docs_tree_past_the_threshold_needs_an_index(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    for name in "abc":
        _write(repo / "docs" / f"{name}.md")
    result = audit_repo(repo, AgentsMdRules(claude_md="any", docs_index_min_files=3))
    index = [f for f in result.findings if f.check == "docs-index"]
    assert [f.path for f in index] == ["docs/README.md"]
    assert all(f.severity is Severity.WARN for f in index)


def test_docs_index_names_each_top_level_entry_it_misses(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [docs](./docs/README.md).\n")
    _write(repo / "docs" / "README.md", "# docs\n\n- [a](a.md)\n- [git](git/README.md)\n")
    _write(repo / "docs" / "a.md")
    _write(repo / "docs" / "missed.md")
    _write(repo / "docs" / "git" / "README.md", "# git\n\n- [stacking](stacking.md)\n")
    _write(repo / "docs" / "git" / "stacking.md")
    _write(repo / "docs" / "estate" / "README.md", "# estate\n")
    result = audit_repo(repo, AgentsMdRules(claude_md="any", docs_index_min_files=3))
    assert sorted(f.path for f in result.findings if f.check == "docs-index") == ["docs/estate", "docs/missed.md"]


def test_folder_without_readme_and_readme_missing_a_sibling(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    _write(repo / "docs" / "plans" / "one.md")
    _write(repo / "docs" / "git" / "README.md", "# git\n\n- [stacking](stacking.md)\n")
    _write(repo / "docs" / "git" / "stacking.md")
    _write(repo / "docs" / "git" / "worktrees.md")
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    assert sorted(f.path for f in result.findings if f.check == "folder-index") == [
        "docs/git/worktrees.md",
        "docs/plans/README.md",
    ]


def test_docs_index_link_through_a_folder_without_readme_counts_for_it(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path, GOOD_ROOT + "\nSee [docs](./docs/README.md).\n")
    _write(repo / "docs" / "README.md", "# docs\n\n- [a](a.md)\n- [plans](superpowers/plans/README.md)\n")
    _write(repo / "docs" / "a.md")
    _write(repo / "docs" / "b.md")
    _write(repo / "docs" / "superpowers" / "plans" / "README.md", "# plans\n\n- [one](one.md)\n")
    _write(repo / "docs" / "superpowers" / "plans" / "one.md")
    _write(repo / "docs" / "git" / "README.md", "# git\n\n- [stacking](stacking.md)\n")
    _write(repo / "docs" / "git" / "stacking.md")
    result = audit_repo(repo, AgentsMdRules(claude_md="any", docs_index_min_files=3))
    # `git` has its own README, so a link past it would not count; here it is simply not linked.
    assert sorted(f.path for f in result.findings if f.check == "docs-index") == ["docs/b.md", "docs/git"]


def test_every_finding_check_is_classified(tmp_path: pathlib.Path) -> None:
    """An unclassified check gets no waiver stub and no place in `--json`'s gate list."""
    from rig.repo_governance.agents_md_audit import GATING_CHECKS, REPORT_ONLY_CHECKS

    repo = _repo(tmp_path)
    for name in "abcdef":
        _write(repo / "docs" / f"{name}.md")
    _write(repo / "docs" / "plans" / "one.md")
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    checks = {f.check for f in result.findings}
    assert {"docs-index", "folder-index"} <= checks
    assert checks <= set(GATING_CHECKS) | set(REPORT_ONLY_CHECKS)


def test_long_code_block_in_docs_is_reported_but_diagrams_are_not(tmp_path: pathlib.Path) -> None:
    repo = _repo(tmp_path)
    body = "\n".join(f"line {n}" for n in range(5))
    long_body = "\n".join(f"line {n}" for n in range(25))
    _write(repo / "docs" / "short.md", f"# s\n\n```bash\n{body}\n```\n")
    _write(repo / "docs" / "long.md", f"# l\n\n```json\n{long_body}\n```\n")
    _write(repo / "docs" / "diagram.md", f"# d\n\n```mermaid\n{long_body}\n```\n")
    result = audit_repo(repo, AgentsMdRules(claude_md="any"))
    blocks = [f for f in result.findings if f.check == "docs-code-block"]
    assert [f.path for f in blocks] == ["docs/long.md"]
    assert "25-line code block at line 3" in blocks[0].message
