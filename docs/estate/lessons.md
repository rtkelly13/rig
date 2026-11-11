# Lessons from the first real run

**Up:** [`estate`](README.md) · [Worktrees](./worktrees.md) · [Shelving](./shelving.md) · [Caches](./caches.md) · [Decision feed](./decisions.md) · [Orca](./orca.md) · [Internals](./internals.md)

What cleaning a real estate in October 2026 taught, and the gaps it left.

`estate` was first used to clean a Windows machine's code estate in October
2026. It took `C:` from 36 GB free to about 65 GB. It also showed that most of
the remaining space sat in worktrees `tidy` cannot touch. The runbook is the
[`disk-cleanup`](../../skills/disk-cleanup/SKILL.md) skill.

On 2026-10-15 the decision feed held 83 `unpushed-worktree` entries (64 GB),
49 `no-pr-worktree` (15 GB), 6 `closed-pr-worktree` (0.4 GB) and 125
`locked-path` (51 GB).

## How worktrees pile up

No single one of these causes explains the space. They compound.

1. **Agents create worktrees, and nothing removes them.** Every agent task
   starts in a fresh `.worktrees/<name>`, and its session ends without cleaning
   up. This machine had 299 registered worktrees.
2. **Squash merges strand the branch.** See
   [Why merged branches need care](./worktrees.md#why-merged-branches-need-care).
3. **`tidy` refuses anything dirty, and until `shelve` nothing else cleared
   it.** The refusal is correct, but without a second tool refusals pile up:
   59 of 299 worktrees on the first pass.
4. **`temp/` holds two kinds of file.** Report runs, pytest basetemps and
   exports write gigabytes of ignored output to it. Hand-written notes,
   scripts and SQL drafts land in the same place. So `temp/` can be neither
   discarded nor kept wholesale.
5. **Each worktree carries its own build output** (`.venv`, `node_modules`,
   `bin/obj`, `packages`, `paket-files`). Three hundred copies of a 1–3 GB
   dependency tree is most of a drive.
6. **The tools could not see it on Windows.** Every size read 0, so `reclaim`
   reported nothing to reclaim while the drive filled.

## Measuring

- **A size of 0 proves nothing.** Native Python on Windows has no `du`, so
  sizing failed silently. A tool that sizes things needs a test that the size
  is not 0.
- **Hardlinks overstate size by 2–3x.** Count a hardlinked file only when every
  link to it is inside the tree being removed (`disksize.du`).
- **Volume-scale questions need the MFT.** A walk of `C:\code` took over 40
  minutes; WizTree reads the MFT in about one.

## Removing on Windows

- **Turn on long paths for every git write.** Without `core.longpaths`,
  `git worktree remove` unregisters the worktree, then stops at the first path
  over 260 characters and leaves it half-deleted. That is a likely source of
  many `locked-path` entries. Every estate git write now passes
  `-c core.longpaths=true`, and `trim.remove_tree` uses the `\\?\` prefix and
  clears read-only files.
- **Python 3.13.0 left `.pytest_cache` unreadable to its owner.** Its
  `os.mkdir(mode=0o700)` wrote a protected ACL granting only SYSTEM and
  Administrators. `git worktree remove` unregisters the worktree, stops at
  that directory and leaves the rest behind: 27 of the first batch's 32
  failures. The owner can still reset the ACL, so `trim.remove_tree` does
  that for each directory it cannot list, and shelve finishes any delete git
  abandons part-way.
- **Some uv `.venv` files deny their user delete.** They are hardlinks into
  uv's cache with a protected ACL, so changing the file's ACL would change the
  cache's copy too. `remove_tree` grants delete-child on the parent directory
  instead. This was the "Invalid argument" in 5 of the first batch's failures.
- **`git worktree remove` leaves pnpm's junctions behind.** A pnpm worktree
  survives as thousands of junctions in empty directories (4,860 links in one
  case). `sweep_link_skeleton` unlinks them without following them, and only
  when no regular file remains.
- **A worktree can hold another repository.** One `data-platform` worktree had
  another clone's worktree registered inside an ignored directory, and
  `git worktree remove --force` would have deleted it. Shelve refuses that
  outright.
- **Commit identity is per repository.** A clone with its own `user.email`
  shelves to a different `shelved/<user>/` namespace, so the namespace comes
  from the repository's config.

## Running it for real

None of these showed up in unit tests against a throwaway remote. Each surfaced
the first time a command met the real estate.

| Bug | Symptom | Fix |
|:---|:---|:---|
| Merged-PR lookup capped at 200 | an older PR's worktree looked unmerged | window raised to 1000 |
| `--all` fell through to the cwd | planned one repository, not the estate | fallthrough removed |
| Default root resolved to the worktrees dir | run inside a worktree, it scanned only its siblings | root resolution fixed |
| Junction leftovers | removal left a directory of links behind | `sweep_link_skeleton` |
| Nested repository | removal would delete another repository's worktree | a decision no flag clears |
| Estate outputs counted as hand-written | shelving tool outputs blocked their own worktrees | classified as regenerable |
| Locked `.pytest_cache` | 32 worktrees unregistered but left on disk | `remove_tree` resets the ACL; shelve finishes the delete |

- **Dry-run the whole estate first.** It costs nothing and found most of the
  bugs above.
- **Then apply one real item before the batch.** The single `report-main`
  shelve found the junction leftover and the per-repository email. No dry run
  would have.
- **The regenerable list grows with use.** Each real repository added entries
  (`.husky/_`, `.wireit`, `paket-files`, `logs`, `*.feature.cs`,
  `TestResults`), and every miss turns a removable worktree into a decision.
- **The agent's permission classifier blocks bulk deletes, and it is right to.**
  Go back to the user for each approval rather than around the block.

## Triaging ignored files

Classify by directory first, then by file. Anything under a known output
directory is output. In what remains, a file matching a secret pattern is a
secret, and a small source-like file is hand-written. Check output directories
before secret patterns: name patterns alone flagged `tokens.css` and a
`node_modules/**/Secret.js`. The hand-written set is small (about 11 MB across
47 worktrees decided the fate of 55 GB), so archive it and discard the rest.

## Gaps

| Gap | Next step |
|:---|:---|
| `tidy` probably leaves the same junction skeleton | sweep the link skeleton from `src/Rig.Core/Estate/Tidy.cs` |
| Nothing stops a shelf being merged | a PR check that fails on a `TEMP-SHELVED:` subject |
| Archiving hand-written files is a one-off script | `estate shelve --archive-handwritten <dir>` |
| `unshelve` is proven only in tests | unshelve `report-main` and diff the result |
| `trim` and `reclaim` keep separate regenerable catalogues | one catalogue, shared by `trim`, `reclaim` and `shelve` |
| The feed does not say what blocks a shelve | it would mean planning every unpushed worktree daily; run the `estate shelve <path>` preview instead |
| Worktrees still accumulate | end-of-task worktree removal in the agent workflow |
