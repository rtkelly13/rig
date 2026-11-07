"""Finding the main checkout from any linked worktree (used by `jobs install`)."""

from pathlib import Path


def test_main_checkout_root_is_the_same_from_a_linked_worktree(tmp_path: Path):
    import subprocess

    from desktop_manager.cli_machine import main_checkout_root, same_path

    main_checkout = tmp_path / "main"
    main_checkout.mkdir()
    git = ["git", "-c", "user.name=t", "-c", "user.email=t@example.com", "-C", str(main_checkout)]
    subprocess.run([*git, "init", "-q"], check=True)
    subprocess.run([*git, "commit", "-q", "--allow-empty", "-m", "init"], check=True)
    worktree = tmp_path / "wt"
    subprocess.run([*git, "worktree", "add", "-q", str(worktree)], check=True)

    from_main = main_checkout_root(main_checkout)
    from_worktree = main_checkout_root(worktree)
    assert from_main is not None and from_worktree is not None
    assert same_path(from_main, main_checkout)
    assert same_path(from_worktree, main_checkout)
    assert main_checkout_root(tmp_path) is None
