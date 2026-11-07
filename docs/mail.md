# Gmail triage

`uv run mail` compiles triage rules for the work Gmail into Gmail filters and a Google Apps Script.
It never talks to Gmail itself. The runbook (setup, sweeps, growing the rules) is the vault skill
`.agents/skills/gmail-triage/SKILL.md`; this page records where the pieces live.

## Where things live

| What | Where | In git? |
|---|---|---|
| Compiler and CLI | `src/rig/mail/` | yes |
| Rules (`rules.yaml`), inbox samples, generated `filters.xml` and `triage.gs` | `temp/mail/` in the **main** rig checkout | **no**: they name real senders |
| Backup of `rules.yaml` | Personal Google Drive (not the work one): `My Drive/Dev/gmail-triage/rules.yaml`, synced on the Mac under `~/Library/CloudStorage/GoogleDrive-<personal account>/` | no |
| Running script | Apps Script project **GmailFilter** in the work account (script.google.com → My projects) | no |
| Filters | Gmail → Settings → Filters and Blocked Addresses; the script records the IDs it created | no |
| Schedule | A 15-minute `triage` trigger on that project (`installTrigger` / `removeTrigger`) | no |

Nothing derived from mail (addresses, domains, subjects, samples, rules) is committed anywhere,
the vault included. Tests and docs use placeholders such as `ci@build.example`.

## Recovering the rules

`temp/` is disposable. If `temp/mail/rules.yaml` is gone, copy it back from the Drive backup:

```bash
cp ~/Library/CloudStorage/GoogleDrive-*/"My Drive/Dev/gmail-triage/rules.yaml" temp/mail/rules.yaml
uv run mail check
```

The backup is a copy, not a sync: after changing the rules, copy `rules.yaml` back to the Drive
folder.

## After a rules change

```bash
uv run mail check
uv run mail apps-script -o temp/mail/triage.gs
```

Paste `triage.gs` over `Code.gs` in GmailFilter, save, and run `setup` until it logs `All set.`
The file carries a version hash, so `setup` previews again (changing nothing) and then replaces
only the filters it created. Hand-made filters are untouched.
