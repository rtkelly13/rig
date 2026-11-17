---
name: vault-capture
description: Pull newly-dropped links out of the Slack self-DM into the capture ledger, resolving X/Twitter links to real text. Use when backfilling Slack, catching up the ledger, or when asked what has been captured since a date.
---

> **Load `vault-reference` first** for paths, repo rules and provenance.

# Capture

Links are dropped into a Slack self-DM daily, and into `Inbox/Links.md` in the Drive vault
from the phone. This skill only moves them into the ledger; `vault-triage` decides.

## Read the sources

**Slack:** `slack_read_channel(channel_id="U0A0BSS2XTJ", limit=100, oldest=<unix ts>)`.
Pass the **user id**, not a `D…` channel id: the DM channel id changes when the account
migrates, which is how the previous pipeline died silently for five months. Paginate with
`oldest`. Start from the newest `date` in `data/slack_capture_*.tsv`, or in
`data/slack_backfill_*.tsv` if no capture ledger exists yet.

**`Inbox/Links.md`:** read it in place under `$DRIVE_VAULT_PATH` and never write to it.
A line with a URL is a capture, and text after the URL is a note.

## Resolve X links

About 60% of captures are X links, often the anonymous `x.com/i/status/<id>` form. Fetch
the **thread**, not the single post; no key or login is needed:

```
https://api.fxtwitter.com/2/thread/<id>
```

- **`thread`:** keep only posts by the same `author.screen_name`, the author's reply chain.
  Authors put the link in a follow-up ("repo in the reply 👇"): 55 of 202 posts did on the
  2026-11-17 run, and the single-post lookup missed every one.
- **`status.quote`:** the quoted post's links are targets too.
- **`status.article`:** an X Article's full text. **`status.media.all`:** images.
- **404** means deleted: record it as `deleted`. Retry 429 and 5xx with backoff.

Take links from `raw_text.facets` where `type` is `url` (the `replacement` field), never by
regex over `text`; the regex lost a third of the links on the first backfill. Drop
`x.com`/`twitter.com` links, entity false positives (`ASP.NET`, `Draw.io`) and duplicates
that differ only by a tracking query.

Cache each id to `data/slack_capture_x_resolved_<from>_<to>.json` with `author`, `text`,
`article`, `thread`, `quote`, media URLs and `target_urls`. Triage reads the article text
and images from this file. Flag posts whose wording promises a link ("👇", "link below",
"repo:") but have no `target_urls`. Leave them flagged with a lead, never guessed.

## Write the ledger

Append to `data/slack_capture_<from>_<to>.tsv` as `date<TAB>url`. Dedupe against prior
ledgers and existing notes (`grep -rhoE 'https?://[^ )>"]+' Vault --include='*.md'`).
Never drop a line for looking worthless: triage records a `drop` with a reason.
Hand the ledger and the resolved JSON to `vault-triage`.
