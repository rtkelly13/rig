# Worktree state: `temp/.rig/`

**Up:** [Repo governance](README.md) · [policy-config.md](policy-config.md) · [estate wrapup](../estate/wrapup.md)

Some rig features need to remember things about one worktree between runs, such as when a session
was asked to wrap up and what it answered. That memory belongs to the worktree, not to rig's
checkout or a central database, so it travels with the folder and goes when the folder goes. This
page is the standard for where that state lives and how it is written. `src/rig/worktree_state.py`
implements it, and every feature uses that module rather than choosing its own path.

## The rules

1. **One folder per worktree: `<worktree root>/temp/.rig/`.** It is rig's alone. Nothing else writes
   there, and rig writes nowhere else in a worktree for this purpose. It sits under `temp/` because
   every managed repo already ignores `temp/`. It is a dot folder because a checkout's `temp/` also holds
   worktrees: rig's own live in `temp/rig/<name>`, so `temp/rig/` was taken.
2. **One file per feature: `<feature>.json`.** The file is a JSON object whose `"schema"` is
   `"rig.<feature>/<version>"`, for example `"rig.wrapup/1"`. The feature's own doc specifies the rest.
3. **Absence means "never".** A missing file means the feature has never recorded anything for this
   worktree. Readers treat a file they cannot parse, or one with a schema version they do not know, as
   missing. They never fail because of it.
4. **It must be gitignored.** State must never make a worktree dirty: rig's `settled` and `tidy` checks
   read `git status`. Before every write, the writer asks git (`git check-ignore`). If git would not
   ignore the file, it skips the write and says why. It never edits `.gitignore` itself.
5. **Atomic, disposable writes.** A write goes to a temporary file in the same folder, then is renamed
   over the old one. Deleting `temp/.rig/` is always safe; only history is lost.
6. **Bumping the version breaks readers on purpose.** A reader that sees an unknown version treats the
   file as missing, so a new version starts fresh. Add fields with defaults inside a version; bump the
   version only when old readers would misread the file.

## How rig enforces it

- **Ignored in every repo.** The `temp` entry in `gitignore_entries` (`config/repo_policies.json`)
  applies to every managed repo, and `rig repo-governance gitignore --apply` appends it where
  it's missing.
- **The audit asks git, not just the file.** `repo-governance gitignore` also runs `git check-ignore`
  on `temp/.rig/`. A repo whose `.gitignore` has the `temp/` line but re-includes the folder with a
  later rule (`!temp/`, or `temp/*` followed by `!temp/.rig/`) shows `NOT IGNORED temp/.rig/` and the
  missing entry `worktree-state`. `--apply` can't fix that safely, so it is reported for a hand edit.
- **Writers check before writing.** A repo outside governance (a work repo, a clone without the
  ignore) never gets a state file. The feature reports `state not recorded: temp/.rig/ is not
  gitignored here` instead. The check and the atomic write live in `WorktreeState` (`src/Rig.Core/Estate/WrapupState.cs`);
  Python (`rig.worktree_state`) only reads.

## Files in use

| file | schema | written by | read by | spec |
|---|---|---|---|---|
| `wrapup.json` | `rig.wrapup/1` | `estate wrapup`, and agents through `estate wrapup report` | `estate wrapup`, the triage inbox | [estate/wrapup.md](../estate/wrapup.md#the-state-file) |

Add a row when a feature starts keeping state here.
