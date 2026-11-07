---
name: vault-capture
description: Pull newly-dropped links out of the Slack self-DM into the capture ledger, resolving X/Twitter links to real text. Use when backfilling Slack, catching up the ledger, or when asked what has been captured since a date.
---

> **Where to run these.** Vault scripts live in `~/code/personal/obsidian-vault`; `cd` there first, or use
> the absolute paths below. This skill is installed globally by `devenv`, so it is
> available from any repo — which means the cwd is not guaranteed to be the vault.

# Capture

Links are dropped into a Slack self-DM all day, every day, and have been since 2019.
That habit is reliable and needs no help. This skill only moves them into the ledger.

## Two capture surfaces

**Slack self-DM** — the desktop habit, running since 2019.

**`Inbox/Links.md` in the Google Drive vault** — the mobile habit. Read it directly at:

```
~/Google Drive/My Drive/Obsidian/Vault/Inbox/Links.md
```

That path is a live Google Drive mount on this machine, so no sync step is needed —
just read the file. **Never write to it.** It has exactly one writer (a person, on a
phone), which is what makes it safe to edit from any device while Drive syncs. Triage
reads and dedupes; it does not tidy, clear or rewrite.

Parse it as: any line containing a URL is a capture; text after the URL on the same
line is a note; lines with no URL are ignored. Dedupe against the ledgers and against
existing vault notes — deleting a line does not un-capture, and re-adding does not
duplicate.

## Read the DM

```
slack_read_channel(channel_id="U0A0BSS2XTJ", limit=100, oldest=<unix ts>)
```

Pass the **user id**, not a `D…` channel id. The DM channel id changes when the Slack
account migrates, and a pinned channel id is precisely how the previous pipeline died
silently for five months. Resolve the channel from the user id every time.

Paginate with `oldest`; each page returns newest-first but pages walk forward in time.
Start from the newest `date` already in `data/slack_backfill_*.tsv`.

## Resolve X links

Roughly 60% of captures are X links, and many are the anonymous `x.com/i/status/<id>`
form carrying no handle or title. Resolve by id — no API key, no login, no browser:

```
https://api.fxtwitter.com/i/status/<id>
```

Gives `tweet.author.screen_name`, `tweet.text`, `tweet.url`. A 404 means the tweet was
deleted; record it as `deleted` rather than dropping it silently. Sleep ~0.4s between
calls and cache results to `data/slack_backfill_x_resolved.json` so re-runs are free.

**Read links from `raw_text.facets`, never by regex over `text`.** Each facet with
`type: "url"` carries a `replacement` field holding the fully expanded destination.
`text` is often empty, and `twitter_card` is only a type string (`player`,
`summary_large_image`, `tweet`) — it does **not** carry the URL. A regex over `text`
silently loses roughly a third of the links; that mistake cost 78 resources on the first
backfill pass.

```python
urls = [f["replacement"] for f in (tweet.get("raw_text") or {}).get("facets", [])
        if f.get("type") == "url"]
```

Discard facet URLs pointing back at `x.com`/`twitter.com`, and filter bare-word false
positives that X's entity detection produces (`ASP.NET`, `Draw.io`, `jeffreys-skills.md`).

## Write the ledger

Append to `data/slack_backfill_<from>_<to>.tsv` as `date<TAB>url`. Dedupe against
existing notes (`grep -rhoE 'https?://[^ )>"]+' Vault --include='*.md'`) and against
prior ledgers before declaring anything new.

Never drop a captured line because it looks worthless — record it with a `drop` verdict
and a reason in triage instead. Silent omission is how you lose the audit trail.

Capture is cheap and safe; stop here and hand the ledger to `vault-triage`.
