"""`rig.proc` against real processes and real temp git repos."""

from __future__ import annotations

import os
import stat
import sys
from dataclasses import dataclass
from pathlib import Path

import pytest

from rig import proc, roots
from rig.proc import GhResult, ProcError, Ran

pytestmark = pytest.mark.ci

_HOOK = """#!/bin/sh
echo ran > "$(git rev-parse --git-dir)/../hook-ran"
"""


@dataclass(frozen=True)
class Estate:
    root: Path
    origin: Path


@pytest.fixture
def estate(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Estate:
    """A code root with personal/, external/ and other/, plus a bare origin outside it."""
    root = tmp_path / "code"
    for folder in ("personal", "external", "other"):
        (root / folder).mkdir(parents=True)
    (root / "AGENTS.md").write_text("# Code root\n", encoding="utf-8")
    monkeypatch.setenv(roots.CODE_ROOT_ENV, str(root))
    # The developer's own git config (a global hooksPath, say) must not change what these tests see.
    monkeypatch.setenv("GIT_CONFIG_GLOBAL", os.devnull)
    monkeypatch.setenv("GIT_CONFIG_NOSYSTEM", "1")

    seed = tmp_path / "seed"
    seed.mkdir()
    _ok(proc.git("init", "-q", "-b", "main", cwd=seed))
    (seed / "README.md").write_text("seed\n", encoding="utf-8")
    _commit(seed, "seed")
    origin = tmp_path / "origin.git"
    _ok(proc.git("clone", "-q", "--bare", str(seed), str(origin), cwd=tmp_path))
    return Estate(root, origin)


def _ok(ran: Ran) -> Ran:
    assert ran.ok, ran.err
    return ran


def _commit(repo: Path, message: str) -> None:
    _ok(proc.git("add", "-A", cwd=repo))
    _ok(proc.git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", message, cwd=repo))


def _clone_with_hook(estate: Estate, folder: str) -> Path:
    clone = estate.root / folder / "repo"
    _ok(proc.git("clone", "-q", str(estate.origin), str(clone), cwd=estate.root / folder))
    hook = clone / ".git" / "hooks" / "post-checkout"
    hook.write_text(_HOOK, encoding="utf-8", newline="\n")
    hook.chmod(hook.stat().st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return clone


# -- hardening -------------------------------------------------------------------


@pytest.mark.parametrize("folder", ["external", "other"])
def test_a_hook_planted_in_an_untrusted_clone_does_not_run(estate: Estate, folder: str) -> None:
    clone = _clone_with_hook(estate, folder)

    _ok(proc.git("checkout", "-q", "-b", "feature", cwd=clone))

    assert not (clone / "hook-ran").exists()


def test_the_same_hook_in_a_personal_clone_runs(estate: Estate) -> None:
    clone = _clone_with_hook(estate, "personal")

    _ok(proc.git("checkout", "-q", "-b", "feature", cwd=clone))

    assert (clone / "hook-ran").exists()


def test_hardening_reaches_a_subfolder_of_an_untrusted_clone(estate: Estate) -> None:
    clone = _clone_with_hook(estate, "external")
    nested = clone / "src"
    nested.mkdir()

    assert _ok(proc.git("config", "--get", "core.hooksPath", cwd=nested)).out.strip() == os.devnull
    assert _ok(proc.git("config", "--get", "core.fsmonitor", cwd=nested)).out.strip() == "false"
    assert _ok(proc.git("config", "--get", "protocol.ext.allow", cwd=nested)).out.strip() == "never"
    assert _ok(proc.git("config", "--get", "submodule.recurse", cwd=nested)).out.strip() == "false"


def test_trusted_clones_get_no_overrides(estate: Estate) -> None:
    clone = _clone_with_hook(estate, "personal")

    assert proc.git("config", "--get", "core.hooksPath", cwd=clone).code == 1


def test_fetch_still_works_under_hardening(estate: Estate, tmp_path: Path) -> None:
    clone = _clone_with_hook(estate, "external")
    pusher = tmp_path / "pusher"
    _ok(proc.git("clone", "-q", str(estate.origin), str(pusher), cwd=tmp_path))
    (pusher / "NEW.md").write_text("new\n", encoding="utf-8")
    _commit(pusher, "new")
    _ok(proc.git("push", "-q", "origin", "main", cwd=pusher))

    _ok(proc.git("fetch", "-q", "origin", cwd=clone))

    assert proc.git_lines("log", "--format=%s", "origin/main", cwd=clone) == ["new", "seed"]


@pytest.mark.parametrize(
    ("relative", "untrusted"),
    [
        ("external/repo", True),
        ("external/skills/lib/src", True),
        ("other/dotnet", True),
        ("personal/rig", False),
        ("external-notes", False),
        ("", False),
    ],
)
def test_untrusted_is_decided_by_the_code_root(estate: Estate, relative: str, untrusted: bool) -> None:
    path = estate.root / relative
    path.mkdir(parents=True, exist_ok=True)

    assert proc.is_untrusted(path) is untrusted


# -- running ---------------------------------------------------------------------


def _python(code: str) -> list[str]:
    return [sys.executable, "-c", code]


def test_missing_executable_is_127_not_an_exception() -> None:
    ran = proc.run(["rig-no-such-binary-xyz"])

    assert ran.code == proc.NOT_FOUND
    assert not ran.ok
    assert "not on PATH" in ran.err


def test_timeout_is_124() -> None:
    ran = proc.run(_python("import time; time.sleep(5)"), timeout=0.2)

    assert ran.code == proc.TIMED_OUT


def test_check_raises_with_the_output() -> None:
    with pytest.raises(ProcError, match="exited 3: boom") as caught:
        proc.run(_python("import sys; sys.stderr.write('boom'); sys.exit(3)"), check=True)

    assert caught.value.ran.code == 3


def test_env_adds_to_the_inherited_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("RIG_PROC_INHERITED", "kept")

    ran = proc.run(
        _python("import os; print(os.environ['RIG_PROC_INHERITED'], os.environ['RIG_PROC_ADDED'])"),
        env={"RIG_PROC_ADDED": "added"},
    )

    assert ran.out.split() == ["kept", "added"]


def test_stdin_is_closed() -> None:
    ran = proc.run(_python("import sys; print(repr(sys.stdin.read()))"))

    assert ran.out.strip() == "''"


def test_undecodable_output_is_replaced_not_raised() -> None:
    ran = proc.run(_python("import sys; sys.stdout.buffer.write(b'ok \\xff\\xfe')"))

    assert ran.ok
    assert ran.out.startswith("ok ")
    assert "�" in ran.out


def test_git_lines_is_none_when_git_fails(tmp_path: Path) -> None:
    assert proc.git_lines("rev-parse", "HEAD", cwd=tmp_path) is None


# -- gh ----------------------------------------------------------------------------


@dataclass(frozen=True)
class _Repo:
    name: str
    stars: int = 0


def test_gh_json_decodes_or_gives_none(monkeypatch: pytest.MonkeyPatch) -> None:
    answers = iter([Ran(0, '{"name": "rig", "stars": 3}', ""), Ran(0, "not json", ""), Ran(1, "", "HTTP 404")])
    monkeypatch.setattr(proc, "gh", lambda *_args, **_kwargs: next(answers))

    assert proc.gh_json(_Repo, "repo", "view") == _Repo("rig", 3)
    assert proc.gh_json(_Repo, "repo", "view") is None
    assert proc.gh_json(_Repo, "repo", "view") is None


def test_gh_feeds_stdin_text_to_an_input_dash_body(monkeypatch: pytest.MonkeyPatch) -> None:
    seen: list[tuple[list[str], str | None]] = []

    def fake_run(argv: list[str], *, stdin_text: str | None = None, **_kwargs: object) -> Ran:
        seen.append((argv, stdin_text))
        return Ran(0, "", "")

    monkeypatch.setattr(proc, "run", fake_run)

    assert proc.gh("api", "-X", "PUT", "repos/o/r/x", "--input", "-", stdin_text='{"a": 1}').ok
    assert seen == [(["gh", "api", "-X", "PUT", "repos/o/r/x", "--input", "-"], '{"a": 1}')]


def test_run_gh_reports_a_missing_gh(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PATH", "")

    assert proc.run_gh("query { viewer { login } }") == GhResult(127, "", "gh is not on PATH")
