# Local scratch space

**Parent:** [`git/`](README.md) · **Rule:** [`AGENTS.md` convention 5](../../AGENTS.md)

Everything that is useful while you work but never part of a commit belongs under
`temp/` at the repo root. That is this clone's tries directory: ephemeral work kept
next to the code it explores, in a directory git already ignores.

`temp/` is in `.gitignore`, so nothing created there needs its own ignore entry, and
nothing committed can come from it.

## What goes where

| Path | Holds | Lifetime |
|:-----|:------|:---------|
| `temp/<branch>` | Worktrees, one per live branch ([worktrees.md](worktrees.md)) | The branch's |
| `temp/tries/` | Ephemeral clones, spikes, one-off attempts | Hours or days |
| `temp/tools/` | Downloaded binaries and harnesses, such as a pinned vectorizer | Until the pin moves |
| `temp/*.db`, `temp/*.log` | Local SQLite, captures, generated reports | Disposable |
| `.worktrees/<branch>` | Alternative worktree root, same rules | The branch's |

One distinction decides every case: **`temp/` can be deleted wholesale and rebuilt from
the repository plus a command.** If losing a file would cost work that exists nowhere
else, it does not belong there.

## Tries

A try is a clone or spike you expect to throw away: evaluating a fork, reproducing an
upstream bug, trialling a dependency bump end to end. Keeping tries in `temp/tries/`
means they sit beside the checkout they inform and vanish with it, instead of
accumulating in a global code root where nothing owns them.

Plain git is the convention; `try(1)` only adds dated directories and a selector:

```bash
git clone <url> temp/tries/<name>     # the convention
try clone <url>                       # if try is pointed at temp/tries
```

`try init` takes an absolute path, so scope it to the checkout from inside it:

```bash
eval "$(try init "$PWD/temp/tries")"
```

A global tries root (Omarchy's default is `~/Work/tries`) is fine for work that spans
repositories. What is not fine is the same try living in both places.

The rules for either:

- **A try is never where work finishes.** Push the branch, or move the commits to a real
  branch, before deleting the directory.
- **Nothing in `temp/tries/` is unique.** That is what makes `rm -rf temp/tries/*` safe;
  if it is not true, the work belongs on a branch already.
- **No secrets.** Ignored is not protected. A token or database in `temp/` still sits
  unencrypted on disk and outside backups; use the keyring or an ignored `.env`.

## Cleaning up

```bash
git worktree list                            # registrations outlive directories
git worktree remove --force temp/<branch>    # refuses a dirty tree first, deliberately
git worktree prune                           # after deleting a directory by hand
rm -rf temp/tries/*                          # safe: see above
```

`rm -rf temp` between worktree sessions is fine, but run `git worktree prune` afterwards
or the stale registrations stay. Check a worktree is disposable
(`git -C temp/<branch> status --short`) before `--force`; a dirty tree is the one case
where scratch has quietly become work.

Disk accounting and orphaned-worktree reclamation live in
[`estate/`](../estate/README.md). This guide only fixes where things go.

## What does not belong in `temp/`

- **Inputs to a build or test.** Fixtures, pinned configs and generated assets that CI
  compares are committed. A local copy in `temp/` makes the local run disagree with CI.
- **Brand assets.** The pipeline's authoritative outputs live under `assets/`; see
  [`brand-assets/`](../brand-assets/README.md). Its `temp/` use is for intermediate
  snapshots only.
- **Anything you would miss.** `temp/` is neither backed up nor synced.
