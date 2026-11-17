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
~/Library/CloudStorage/GoogleDrive-<account>/My Drive/Obsidian/Vault/Inbox/Links.md
```

Google Drive for desktop mounts under `~/Library/CloudStorage/`, one folder per signed-in
account. The vault is in the **personal** account's folder, not the work one, and the old
`~/Google Drive/` shortcut no longer exists. Find it with
`ls -d ~/Library/CloudStorage/GoogleDrive-*/"My Drive/Obsidian/Vault"` and export it as
`DRIVE_VAULT_PATH`. Keep the account out of anything committed: the folder name is an
email address.

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
Start from the newest `date` already in `data/slack_capture_*.tsv` (older runs are
`data/slack_backfill_*.tsv`).

## Resolve X links

Roughly 60% of captures are X links, and many are the anonymous `x.com/i/status/<id>`
form carrying no handle or title. Resolve by id, with no API key, login or browser, and
**fetch the thread, not the single post**:

```
https://api.fxtwitter.com/2/thread/<id>
```

The response has `status` (the post) and `thread` (the conversation around it). Keep from
`thread` only the posts by the same `author.screen_name`: that is the author's own reply
chain. Authors routinely put the link in a follow-up ("repo in the reply 👇"), so the
single-post lookup (`/i/status/<id>`) misses it. On the 2026-11-17 capture, 55 of 202 X
posts had their outgoing link only in a self-reply.

From `status`, also keep:

- **`quote`**: the quoted post. Its URL facets are targets too (4 more links on that run).
- **`article`**: the full text of an X Article. Save it; triage writes notes from it, so
  the summary is of text actually in hand.
- **`is_note_tweet`**: a long post. The text is the content.
- **`media.all`**: photos and video thumbnails (see below).

A 404 means the post was deleted; record it as `deleted` rather than dropping it
silently. Retry 429 and 5xx with backoff, sleep ~0.4s between calls, and cache each id to
`data/slack_capture_x_resolved_<from>_<to>.json` so re-runs fetch only what is missing.

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

Record `target_urls` as the deduped union of the facet URLs from the post, the author's
reply chain and the quoted post. Collapse links that differ only by a tracking query
(one post produced about 20 copies of one link with different `?def=` values).

**Check for a missed link.** Flag posts whose wording promises one ("👇", "link below",
"repo:", "check it out") but that have no `target_urls`. Most flags are false alarms
(essays that use the words), but some are real: the link sits in a reply outside the
author's chain. Leave those flagged with a lead, never guessed.

### Images

When the picture is the point (a diagram, a screenshot explainer, a TIL card), the image
URL is the durable part: X posts get deleted and linked pages go 404. Photo URLs in
`media.all` take `?name=orig` for full size:

```
https://pbs.twimg.com/media/<key>.jpg?name=orig
```

Triage decides which to keep. Save them to `Vault/Attachments/x/<status-id>-<n>.<ext>` and
embed beside the entry as `![[<status-id>-<n>.png|480]]`. Videos are not saved; keep the
post link.

## Write the ledger

Append to `data/slack_capture_<from>_<to>.tsv` as `date<TAB>url`. Dedupe against
existing notes (`grep -rhoE 'https?://[^ )>"]+' Vault --include='*.md'`) and against
prior ledgers before declaring anything new.

Never drop a captured line because it looks worthless — record it with a `drop` verdict
and a reason in triage instead. Silent omission is how you lose the audit trail.

Capture is cheap and safe; stop here and hand the ledger to `vault-triage`.
