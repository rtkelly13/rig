---
name: vault-reconcile
description: Reconcile the Google Drive vault with the git vault after editing on mobile. Use when the two copies have drifted, before a triage pass, or when notes appear in one place and not the other.
---

> **Load `vault-reference` first** for paths, repo rules and provenance.

# Reconcile

Two copies of the vault exist on purpose: the Drive copy (`$DRIVE_VAULT_PATH`), which Obsidian
and the phone edit, and the git copy, the record. They drift in both directions. This brings
them back to one thing, deterministically, and **never deletes**: a file that vanished from
one side is indistinguishable from one that has not synced yet, so both are kept.

The script's default Drive path is stale, so **every run passes `--drive`**. Without it, every
note reports as repo-only:

```bash
R=(uv run --no-project ~/code/personal/obsidian-vault/scripts/vault-reconcile.py --drive "$DRIVE_VAULT_PATH")
"${R[@]}"            # report only; always start here
```

## Run it in this order

The order is load-bearing. Each step changes what the next one sees.

**0. Back up Drive first.** It is live data, and steps 1 and 3 write to it.
`rig vault drive-backup` lists the notes whose Drive copy differs from main; `--apply`
commits those Drive copies onto main and pushes them as `backup/drive-diff-<stamp>`, so
anything a conflict overwrites stays recoverable in git. It never touches the checkout.
The tarball keeps the mtimes the trap below needs.

```bash
rig vault drive-backup --apply
tar -czf /tmp/drive-vault-$(date +%Y%m%d-%H%M%S).tar.gz -C "$(dirname "$DRIVE_VAULT_PATH")" Vault
```

Never merge the branch: it is a backup to restore from, not a change. Delete it once the
reconcile is verified and nothing needed restoring.

**1. Unescape wikilinks.** `"${R[@]}" --fix-links --apply`

mdformat escapes wikilinks as `\[[target]\]`, which Obsidian will not resolve. The first
run found **3,386 occurrences across 88 files** in Drive and none in the repo. Do this
first: it collapses false conflicts, because a file differing *only* by escaping becomes
identical and needs no decision.

**2. Pull Drive-only notes.** `"${R[@]}" --pull --apply` — purely additive, nothing can be lost.

**3. Decide the conflicts.** `"${R[@]}" --conflicts {report,newest,repo,drive}`, default `report`.

**4. Push repo-only notes.** `"${R[@]}" --push --apply`

**5. Verify.** `"${R[@]}"` with no other flags. Success is `differ 0`, `drive-only 0`, `repo-only 0`.

## The trap: step 1 contaminates mtime

**`--fix-links` rewrites files, so those 88 Drive files get a fresh mtime and start
looking newer than their repo counterparts.** `--conflicts newest` after step 1 will
wrongly hand them to Drive.

Decide conflicts on evidence, not on the mtimes you just changed. The backup from step 0
holds the uncontaminated times:

```bash
uv run --no-project python - <<'PY'
import tarfile, pathlib, datetime as dt
bk = tarfile.open("/tmp/drive-vault-<stamp>.tar.gz")
repo = pathlib.Path.home() / "code/personal/obsidian-vault/Vault"
pre = {m.name.split('Vault/',1)[-1]: m.mtime for m in bk.getmembers()
       if m.name.endswith('.md') and '.obsidian' not in m.name}
newer = [(r, t) for r, t in pre.items()
         if (repo/r).is_file() and t > (repo/r).stat().st_mtime]
print(f"genuinely newer on Drive: {len(newer)}")
for r, t in sorted(newer)[:20]:
    print(" ", dt.date.fromtimestamp(t), r)
PY
```

The first run returned **0**, so `--conflicts repo` was right on evidence. **Re-check it
every time:** any file it lists needs looking at before you pick a side, or `--conflicts
repo` discards real phone edits.

## Unreadable paths

Dangling symlinks and un-materialised cloud files list but fail to open. They are
reported and skipped, never copied or repaired: a dead symlink is a decision, not a defect.

## What not to do

- **Don't sync `.obsidian/`.** Plugins reach Drive through `devenv`, and `workspace.json`
  churns on every pane change.
- **Don't run a markdown formatter over `Vault/`.** That is what broke 88 files.
- **Don't add two-way sync software.** The copies converge when this runs; continuous
  sync over a git working tree is how notes get lost.
