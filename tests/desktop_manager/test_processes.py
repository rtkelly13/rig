"""Idle-vs-busy shell detection, which is what makes terminal cleanup safe.

Warp and Windows Terminal share one process across every window, so cleanup cannot
work by PID or image name without risking the session that started the restore. The
discriminator is the per-window shell: empty means disposable, busy means in use.
"""

from __future__ import annotations

import pytest

from desktop_manager.processes import (
    ProcessInfo,
    idle_shell_pids_under,
    is_idle_shell_under,
    pids_with_children,
    snapshot_processes,
)

pytestmark = pytest.mark.ci


def _tree() -> list[ProcessInfo]:
    """One Warp host with three shells: two idle, one running a command."""
    return [
        ProcessInfo(pid=100, parent_pid=4, image_name="warp.exe", created_at=1000.0),
        ProcessInfo(pid=201, parent_pid=100, image_name="pwsh.exe", created_at=1100.0),
        ProcessInfo(pid=202, parent_pid=100, image_name="pwsh.exe", created_at=1200.0),
        ProcessInfo(pid=203, parent_pid=100, image_name="pwsh.exe", created_at=1300.0),
        ProcessInfo(pid=999, parent_pid=203, image_name="node.exe", created_at=1350.0),
        ProcessInfo(pid=300, parent_pid=4, image_name="Code.exe", created_at=1000.0),
        ProcessInfo(pid=301, parent_pid=300, image_name="pwsh.exe", created_at=1100.0),
    ]


class TestIdleShellDetection:
    def test_shells_with_a_running_command_are_never_reaped(self):
        """This is the guard that protects a live session.

        A window running Claude has a child process; a window sitting at a prompt does
        not. Nothing else distinguishes them from outside the terminal host.
        """
        assert idle_shell_pids_under("warp.exe", processes=_tree()) == [201, 202]

    def test_shells_of_other_hosts_are_ignored(self):
        """Only the named host's own children count, or cleanup reaches into other apps."""
        assert 301 not in idle_shell_pids_under("warp.exe", processes=_tree())

    def test_created_after_limits_cleanup_to_this_run(self):
        """A shell that predates the run is the user's, not ours to close."""
        assert idle_shell_pids_under("warp.exe", created_after=1150.0, processes=_tree()) == [202]

    def test_shells_are_returned_oldest_first(self):
        tree = list(reversed(_tree()))

        assert idle_shell_pids_under("warp.exe", processes=tree) == [201, 202]

    def test_missing_host_yields_nothing(self):
        assert idle_shell_pids_under("wt.exe", processes=_tree()) == []

    def test_non_shell_children_are_not_candidates(self):
        tree = [
            ProcessInfo(pid=100, parent_pid=4, image_name="warp.exe"),
            ProcessInfo(pid=201, parent_pid=100, image_name="warp-helper.exe"),
        ]

        assert idle_shell_pids_under("warp.exe", processes=tree) == []

    def test_host_matching_is_case_insensitive(self):
        tree = [
            ProcessInfo(pid=100, parent_pid=4, image_name="WarpStable.exe"),
            ProcessInfo(pid=201, parent_pid=100, image_name="PWSH.EXE"),
        ]

        assert idle_shell_pids_under("warpstable.exe", processes=tree) == [201]

    def test_shells_without_a_creation_time_are_not_claimed_by_created_after(self):
        """An unqueryable creation time must not be read as "started on our watch"."""
        tree = [
            ProcessInfo(pid=100, parent_pid=4, image_name="warp.exe"),
            ProcessInfo(pid=201, parent_pid=100, image_name="pwsh.exe", created_at=None),
        ]

        assert idle_shell_pids_under("warp.exe", created_after=0.0, processes=tree) == []

    def test_pids_with_children_reports_every_parent(self):
        assert pids_with_children(_tree()) == {4, 100, 203, 300}


@pytest.mark.windows
class TestLiveSnapshot:
    def test_snapshot_includes_this_process_with_its_parent(self):
        import os

        snapshot = snapshot_processes()
        mine = [process for process in snapshot if process.pid == os.getpid()]

        assert len(mine) == 1
        assert mine[0].image_name.casefold().startswith("python")
        assert mine[0].parent_pid > 0

    def test_creation_times_are_populated_on_request(self):
        import os
        import time

        snapshot = snapshot_processes(with_times=True)
        mine = next(process for process in snapshot if process.pid == os.getpid())

        assert mine.created_at is not None
        assert 0 < mine.created_at <= time.time() + 1

    def test_this_interpreter_is_not_reported_as_an_idle_shell(self):
        """A sanity check on the live path: python is not a shell under a terminal host."""
        import os

        assert is_idle_shell_under(os.getpid(), "warp.exe") is False
