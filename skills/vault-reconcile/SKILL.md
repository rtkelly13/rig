---
name: vault-reconcile
description: Reconcile the Google Drive vault with the git vault after editing on mobile. Use when the two copies have drifted, before a triage pass, or when notes appear in one place and not the other.
---

> **Where to run these.** Vault scripts live in `~/code/personal/obsidian-vault`; `cd` there first, or use
> the absolute paths below. This skill is installed globally by `devenv`, so it is
> available from any repo — which means the cwd is not guaranteed to be the vault.

# Reconcile

Two copies of the vault exist on purpose:

- **Google Drive** — `~/Google Drive/My Drive/Obsidian/Vault`, what Obsidian opens and
  what the phone edits
- **git** — `~/code/personal/obsidian-vault/Vault`, the record

They drift in both directions. This brings them back to one thing, deterministically,
and **never deletes** — a file that vanished from one side is indistinguishable from one
that has not synced yet, so both are kept.

```bash
python3 ~/code/personal/obsidian-vault/scripts/vault-reconcile.py            # report only; always start here
```

## Run it in this order

The order is load-bearing. Each step changes what the next one sees.

**0. Back up Drive first.** It is live data, and steps 1 and 3 write to it.

```bash
tar -czf /tmp/drive-vault-$(date +%Y%m%d-%H%M%S).tar.gz \
  -C ~/"Google Drive/My Drive/Obsidian" Vault
```

**1. Unescape wikilinks.** `python3 ~/code/personal/obsidian-vault/scripts/vault-reconcile.py --fix-links --apply`

mdformat escapes wikilinks as `\[[target]\]`, which Obsidian will not resolve. The first
run found **3,386 occurrences across 88 files** in Drive and none in the repo. Do this
first: it collapses false conflicts, because a file differing *only* by escaping becomes
identical and needs no decision.

**2. Pull Drive-only notes.** `--pull --apply` — purely additive, nothing can be lost.

**3. Decide the conflicts.** `--conflicts {report,newest,repo,drive}`, default `report`.

**4. Push repo-only notes.** `--push --apply`

**5. Verify.** Re-run with no flags. Success is `differ 0`, `drive-only 0`, `repo-only 0`.

## The trap: step 1 contaminates mtime

**`--fix-links` rewrites files, so those 88 Drive files get a fresh mtime and start
looking newer than their repo counterparts.** `--conflicts newest` after step 1 will
wrongly hand them to Drive.

Decide conflicts on evidence, not on the mtimes you just changed. The backup from step 0
holds the uncontaminated times:

```bash
python3 - <<'PY'
import tarfile, pathlib, datetime as dt
bk = tarfile.open("/tmp/drive-vault-<stamp>.tar.gz")
repo = pathlib.Path("Vault")
pre = {m.name.split('Vault/',1)[-1]: m.mtime for m in bk.getmembers()
       if m.name.endswith('.md') and '.obsidian' not in m.name}
newer = [(r, t) for r, t in pre.items()
         if (repo/r).is_file() and t > (repo/r).stat().st_mtime]
print(f"genuinely newer on Drive: {len(newer)}")
for r, t in sorted(newer)[:20]:
    print(" ", dt.date.fromtimestamp(t), r)
PY
```

On the first reconciliation this returned **0** — no Drive-side edit was newer — so
`--conflicts repo` was correct on evidence rather than preference. **Re-check it every
time.** If it returns anything, those files need looking at individually before you pick
a side; `--conflicts repo` would discard genuine phone edits.

## Unreadable paths

Dangling symlinks and un-materialised cloud files appear in a directory listing but fail
to open. They are reported, skipped, and never copied, so one bad path cannot abort a run
part-way. The first run found one: `Inbox/staging.md`, a symlink into the retired
shared-utilities staging pipeline.

Nothing repairs these automatically — a dead symlink is a decision, not a defect.

## What not to do

**Don't sync `.obsidian/`.** Skipped deliberately. Plugin binaries are large, and
`workspace.json` churns on every pane change. `community-plugins.json` is tracked in git
and installed by `devenv`; that is the sync path for plugins.

**Don't run a markdown formatter over the vault.** That is what broke 88 files. `md-lint`
is for `docs/`, not `Vault/`.

**Don't reach for two-way sync software.** The copies converge at a point in time by
running this; between runs, Drive is a capture surface and git is the record. Continuous
bidirectional sync over a git working tree is how notes get lost.
