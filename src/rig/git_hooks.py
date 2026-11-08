"""The pre-push hook rig installs in every clone, shared by repo governance and the estate.

`repo-governance enforce` installs it across the estate; `estate history setup` installs it in
a clone whose repo was restarted. Both read the template from here, so neither feature imports
the other.

The hook does three things:

- blocks direct pushes to `main` unless `ALLOW_MAIN_PUSH=1`;
- blocks pushing history from before a restart. When a repo is restarted without history, its
  clone carries `rig.history.root` and `rig.history.slug` in git config (`estate history setup`
  writes them). A push to that slug whose commit does not descend from the root is refused. A
  clone without those keys is unaffected;
- hands the push to git-lfs, so LFS content is uploaded with the commits.
"""

from __future__ import annotations

import pathlib

#: Marks a hook that blocks direct pushes to main.
MAIN_BLOCK_MARKER = "DIRECT PUSH TO MAIN BLOCKED"
#: Marks a hook that refuses history from before a restart.
HISTORY_GUARD_MARKER = "OLD HISTORY PUSH BLOCKED"
#: Marks a hook that also hands the push to git-lfs. pre-push is the hook git-lfs
#: uploads LFS content from, so a hook without it pushes pointers and no content.
LFS_HANDOFF_MARKER = "git lfs pre-push"

#: Git config keys the history guard reads; written by `estate history setup`.
HISTORY_ROOT_KEY = "rig.history.root"
HISTORY_SLUG_KEY = "rig.history.slug"

PRE_PUSH_HOOK_CONTENT = """#!/usr/bin/env bash
# Enforced by align-repo-conventions
# Prevents accidental direct pushes to main branch without explicit bypass,
# refuses history from before a repo restart (rig.history.* in git config),
# then hands the push to git-lfs so LFS content is uploaded with the commits.

# Git passes the refs being pushed on stdin; keep them for git-lfs.
refs=$(cat)
remote_url="$2"
history_root=$(git config --get rig.history.root || true)
history_slug=$(git config --get rig.history.slug || true)
zero="0000000000000000000000000000000000000000"

while read local_ref local_oid remote_ref remote_oid
do
	if [ -n "$history_root" ] && [ -n "$history_slug" ] && [ -n "$local_oid" ] && [ "$local_oid" != "$zero" ]; then
		case "$remote_url" in
			*[:/]"$history_slug"|*[:/]"$history_slug".git|*[:/]"$history_slug"/)
				if ! git merge-base --is-ancestor "$history_root" "$local_oid" 2>/dev/null; then
					echo "🛑 OLD HISTORY PUSH BLOCKED: $local_ref predates the restart of $history_slug."
					echo "Port it first: uv run estate history port <branch> (from the rig checkout)."
					exit 1
				fi
				;;
		esac
	fi
	if [ "$remote_ref" = "refs/heads/main" ]; then
		if [ "$ALLOW_MAIN_PUSH" != "1" ]; then
			echo "🛑 DIRECT PUSH TO MAIN BLOCKED by repository governance policy!"
			echo "Push your changes to a feature branch and merge via PR (gh pr merge --squash)."
			echo "Override for emergency push: ALLOW_MAIN_PUSH=1 git push origin main"
			exit 1
		fi
	fi
done <<< "$refs"

if command -v git-lfs >/dev/null 2>&1; then
	printf '%s\\n' "$refs" | git lfs pre-push "$@" || exit $?
fi

exit 0
"""


def hook_is_current(content: str) -> bool:
    """True when *content* is a pre-push hook with every guard this version installs."""
    return all(marker in content for marker in (MAIN_BLOCK_MARKER, HISTORY_GUARD_MARKER, LFS_HANDOFF_MARKER))


def pre_push_hook(hooks_dir: pathlib.Path) -> pathlib.Path:
    return hooks_dir / "pre-push"


def install_pre_push_hook(hooks_dir: pathlib.Path) -> bool:
    """Write the current hook into *hooks_dir*. False when it was already current."""
    hook_file = pre_push_hook(hooks_dir)
    if hook_file.exists() and hook_is_current(hook_file.read_text(encoding="utf-8")):
        return False
    hook_file.write_text(PRE_PUSH_HOOK_CONTENT, encoding="utf-8", newline="\n")
    hook_file.chmod(0o755)
    return True
