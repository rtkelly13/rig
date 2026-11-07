# Chrome tabs via UI Automation

`desktop-manager chrome-tabs` reads and closes tabs in the running Chrome
through Windows UI Automation (UIA). It needs no extension and no debugging
port. `group` gathers GitHub tabs into one tab group per repo, and Google,
Asana, YouTrack and Claude tabs into one group per site. `organise` prunes, then
groups. `prune` does three jobs in one pass:

- **PR pruning** closes GitHub pull-request tabs that are merged, duplicated, or
  superseded by a lower PR in a stack or merge queue.
- **Site dedupe** closes duplicate tabs of any other page, using per-site title
  rules. Claude artifacts, YouTrack, Gmail, Calendar, Google Docs and AWS
  consoles are among the sites covered.
- **Memory budget** (`--max-memory GB`) runs after the other two rules. It
  closes the lowest-ranked tabs, biggest first, until the memory Chrome reports
  for the remaining tabs fits the budget.

`rank` lists every tab with its keep score and the reasons for it. The memory
budget closes tabs in this order, from the bottom of the list up. Video calls
are always kept, even when muted with the camera off.

```powershell
desktop-manager chrome-tabs list [--profile "Profile 4"] [--prs-only] [--json]
desktop-manager chrome-tabs prune --dry-run      # print the plan, grouped by reason
desktop-manager chrome-tabs prune                # close the planned tabs
desktop-manager chrome-tabs prune --no-sites     # PR rules only
desktop-manager chrome-tabs prune --no-github    # no GitHub calls: site dedupe only
desktop-manager chrome-tabs prune --dry-run --max-memory 5   # also fit tabs into 5 GB
desktop-manager chrome-tabs rank [--limit 20] [--no-github] [--json]
desktop-manager chrome-tabs group --dry-run      # print the grouping plan
desktop-manager chrome-tabs group [--min-size 2] # group; takes the mouse briefly
desktop-manager chrome-tabs group --this-desktop # leave windows on other desktops alone
desktop-manager chrome-tabs organise [--dry-run] # prune, then group, one window at a time
desktop-manager chrome-tabs organise --unattended [--idle-minutes 5]  # what the schedule runs
desktop-manager chrome-tabs organise --consolidate-saved  # after a new group, delete closed duplicates
desktop-manager chrome-tabs saved-groups        # list saved groups and the consolidation plan
desktop-manager chrome-tabs saved-groups --consolidate  # delete the closed duplicates
desktop-manager chrome-tabs install             # `chrome-tabs` command on PATH
desktop-manager chrome-tabs uninstall
```

## Grouping

`group` puts each tab with a key into a tab group named after that key:

- **repos**: each GitHub tab (PRs, issues, files, lists, Actions runs, repo
  home pages) is keyed by its repo, titled `owner/repo` when two owners share a
  repo name;
- **sites**: `Google` (Gmail, Calendar, Docs, Sheets, Slides, Drive, Search),
  `Asana`, `YouTrack` (`… : PROJ-123` and YouTrack pages), `Claude`
  (artifacts and the claude.ai shells), `AI` (ChatGPT, OpenAI, Gemini,
  opencode) and `Dev` (developer tools and docs: npm, NuGet, Vercel, Renovate
  and the like, and GitHub pages that name no repo). Title shapes are rules in
  code. Hosts are listed per group in `config/chrome-site-groups.json`: add a
  domain there to put a site in a group. A host joins the group of the longest
  domain it equals or ends with, so `gemini.google.com` is AI, not Google.

UIA gives titles, not URLs, so a bare title such as a Claude artifact's name
says nothing about its site. Titles that match no rule are looked up in the
profile's Chrome history (`history.hosts_by_title`, read-only). A title whose
visits all went to one host takes that host's site; a title seen on several
hosts is left alone.

Where the driver reports each tab's URL (macOS), the URL is used instead of
history. It can also name a repo the title does not: a `github.com/owner/repo/…`
page, a local app on `localhost` or `127.0.0.1` whose port is inside a
project's claimed range, or a `*.vercel.app` deployment named after one of the
project's Vercel projects ([local ports and Vercel names](../estate/url-ownership.md)).
Such tabs join that repo's group.

Video-call tabs (`site_rules.CALLS`: Meet, Zoom, Teams, Slack huddles and so
on) have no key and never move. Nor do pinned tabs, or tabs already in a group
with another title.

Each key is consolidated into one window per profile:

- every repo group lives in the **review window**: the window holding the most
  GitHub tabs (the earlier window in screen order on a tie). Repo groups in
  other windows are dissolved and their tabs move there;
- a site's home window is the one holding its existing group, or else the one
  with the most loose tabs of that site;
- a new group is created once the profile has `--min-size` loose tabs of that
  key; a lone tab still joins an existing group;
- tabs in other windows move into the home group through "Add tab to group",
  which carries them across windows. When the home window has no tab of a new
  group yet, one tab is first moved there with "Move tab to another window",
  and the group is created on it (`new in wN` in the plan);
- duplicate groups with the same title are merged: the smaller ones are
  ungrouped first (the plan's `Ungroup first` block), then their tabs join the
  biggest;
- a key is left alone, and listed as `Left …`, when a duplicate group also
  holds other tabs.

Windows on every virtual desktop take part. For the run, windows on other
desktops are moved onto the current one, because clicking into a window on
another desktop would switch desktops under you, and they are moved back
afterwards. `--this-desktop` skips them instead, and leaves a key whose group
is on another desktop alone.

Chrome saves every tab group to the bookmarks bar (saved tab groups).
Ungrouping a saved group asks "Ungroup tab group?"; the adapter confirms
"Ungroup" and never ticks "Don't ask again". Dissolving a duplicate therefore
removes that duplicate's saved group too. Chrome ignores a dialog's buttons for
a moment after it appears, so the confirm is pressed once the dialog has been
up for 0.8 s, and retried no faster than that.

Chrome's group submenu lists every group in the profile by title only, so the
adapter refuses a join when more than one entry has the wanted title.

Chrome has no UIA pattern for grouping, so each window is brought to the
foreground and the tab is right-clicked; "Add tab to group" is picked from the
context menu, and a new group's name is typed. Your cursor and the previous
foreground window are put back afterwards. Don't use the mouse while it runs.
It stops at the first surprise, pressing Escape. Duplicates are ungrouped
first, window by window. Then windows run one at a time: windows that create
groups first, then the rest in screen order. Within a window the order is:
create groups, join the window's own groups, then move tabs to other windows.

## Organise

`organise` runs both. It closes the planned tabs window by window, putting each
window's active tab back, then reads the windows again and groups what is left.
The close plan is still made across all windows, because duplicates span
windows. Duplicates are closed before grouping, so they are never grouped.

With `--unattended`, which is what the scheduled task runs:

- nothing runs while a call is live, so a meeting never sees tabs close or
  windows jump. A call is live when a tab is recording (camera or microphone)
  or sharing a tab or the desktop, when a call site's tab is audible, or when
  Windows' privacy settings show Chrome using the microphone or camera right
  now. That last signal catches a muted call and a Meet app window, which
  has no tab strip to read;
- pruning holds back the selected tab of every window and every tab in the
  foreground window, as `prune --unattended` does;
- grouping starts only when the keyboard and mouse have been idle for
  `--idle-minutes` (default 5), and never while a call is live. Otherwise it
  is deferred to the next run;
- grouping stops at the first key press or mouse move after one of its own
  steps. It then opens a Notepad note, `chrome-tidy-paused.txt`, asking you to
  leave the machine alone. Once the keyboard and mouse have been idle for 30
  seconds it plans again and carries on. It waits up to 5 minutes for that,
  and retries at most twice. After that the rest is left for the next run. A
  run opens the note once, however often it is interrupted.

Closing, grouping and saved-group deletes each note the virtual desktop you are
on before they start. If bringing a window forward, or sending one home, took
you to another desktop, they switch back when they finish.

## Saved groups

Chrome saves every tab group, and each saved group shows as a chip on the
bookmarks bar, named `<title> group – Opened` or `<title> group – Closed`.
Closing a group keeps its saved copy. Creating a group later with the same
title saves a second copy rather than reopening the first. Grouping makes new
groups, so a profile collects closed duplicates: three "Google" chips, one of
them open.

`saved-groups` lists each profile's chips and plans one saved copy per title:

- when a title has an open copy, every closed copy is deleted;
- when it has only closed copies, every copy but the leftmost is deleted;
- an open copy is never deleted, and a title with one copy is left alone. A
  title open more than once is only reported.

`--consolidate` makes the deletes. `organise --consolidate-saved` does the same
after grouping, but only when grouping created a new group: joining an open
group never saves a duplicate, so a run that only joined skips the bar. A
consolidation that failed a delete, or deleted anything (which can bring chips
out of the overflow), lists its profile in a `saved-groups.pending` marker next to
the traces directory, and later runs consolidate again until that profile's bar
is read with nothing to delete. The profile is listed before the deletes start,
so a run that crashes part way is retried too. The
scheduled task passes it. Each delete opens the chip's context menu through UIA's
`ShowContextMenu` and picks "Delete group". This needs no focus and leaves the
mouse alone. It records the saved tabs the menu lists and prints them, because
deleting the saved group forgets them. It never invokes a closed chip, which
would reopen the group with its old tabs. It re-reads the bar before each
delete and refuses to remove the last copy of a title.

All windows of a profile show the same chips in the same order, as many as fit
their width, so the widest window's bar is read. The "Tab groups" overflow menu
only opens on a foreground window, so chips that fit nowhere are not seen until
deletes free room.

## Window order

`prune`, `group` and `organise` visit windows in screen order: windows on the current
desktop first, then monitor by monitor from left to right, then top to bottom
and left to right on each monitor. Closing selects each tab first, so working
through one window at a time means each window changes once, instead of
flickering as the closes move between windows.

## Installing

The scheduled `organise --unattended` run (see [Organise](#organise)) belongs to
`desktop-manager jobs install`: its Tick task runs it, as you, only while you are
logged on, never overlapping (see [desktop-manager/jobs.md](desktop-manager/jobs.md)).
The output goes to `organise.log` in `%LOCALAPPDATA%\shared-utilities\chrome-tabs`,
rotated at 1 MB.

`chrome-tabs install` writes a `chrome-tabs.cmd` shim to `~/.local/bin` that runs the
installed runtime's `current` copy, so it follows `runtime install` and `rollback`
(see [runtime.md](runtime.md)). `uninstall` removes the shim, and the snapshot
environment older installs built, and keeps the logs.

## Traces

Every `organise` run, scheduled or by hand, is traced. One root span,
`chrome_tabs.organise`, carries the trigger, the outcome and the counts. Beneath
it are child spans for each step:

- reading the windows over UIA, once per phase;
- the GitHub lookups, one `github.graphql` span per query;
- planning which tabs to close;
- closing tabs, per window;
- planning the groups;
- the wait for an idle machine, with a `check` event for every look;
- the re-plan;
- grouping, per window.

An exception leaving a span is recorded on it as an `exception` event.

The outcome is `ok`, `partial`, `failed`, `dry_run` or `skipped_live`. A tab that
would not close is still open, so the next run plans it again. That run is
`partial`: it ends with a `Warning: N tab(s) not closed` line and exits 0. Grouping
that your input stopped is not a failure: the next run carries on. Grouping or
consolidation that stopped part-way by itself is `failed` and exits 1, unattended
or not.

The root span's `group.outcome` is `grouped`, `nothing_to_group`, `deferred`,
`interrupted` (input stopped it and it never finished) or `stopped` (it failed).
`group.retries` counts the retries after input.

The spans go to `%LOCALAPPDATA%\shared-utilities\chrome-tabs\traces\<YYYY-MM-DD>.ndjson`,
one file per day. Each line is an OTLP-JSON envelope, the same format the
repo-wide `rig.tracing` writes. The process flushes them every 30
seconds and again on exit. Just before the task's time limit, the scheduled
entry point also writes the spans still running, marked `span.incomplete`, so a
killed run still shows up.

The first scheduled run of each day deletes day files more than 30 days old.
There is no separate cleanup task.

```text
desktop-manager chrome-tabs traces runs [--days 7] [--limit 20] [--json]   # recent runs, newest first
desktop-manager chrome-tabs traces show [RUN_ID]                          # one run's span tree (default: latest)
desktop-manager chrome-tabs traces summary [--days 7] [--json]            # success rate, waits, commonest blocks
desktop-manager chrome-tabs traces prune [--keep-days 30] [--dry-run]     # retention, by hand
```

Each command takes `--dir` to read another directory.

## Modules

| Module | Role | Public API |
|---|---|---|
| `shared_utilities/github_prs.py` | GitHub resolver and chain building. No desktop or UIA imports. | `PrRef`, `PrState`, `RepoChain`, `Resolution`, `resolve(refs)`, `resolve_pr_states(refs)`, `build_chains(open_prs)` |
| `desktop_manager/chrome/tabs.py` | Pure tab and window records, plus accessible-name parsing | `ChromeTab`, `ChromeWindow`, `TabName`, `parse_tab_name(name)` |
| `desktop_manager/chrome/uia.py` | Reusable UIA adapter. Not tied to PRs. | `ChromeUia().list_windows()`, `.find_tab_element(tab)`, `.select_tab(tab)`, `.close_tab(tab)`, `.group_tab(tab, title=, new=, home_hwnd=)`, `.ungroup(tab, title=)`, `.move_to_window(tab, target_hwnd)`, `.saved_groups(hwnd, profile)`, `.delete_saved_group(hwnd, profile, title)`, `.foreground(hwnd)`, `.restore_selection(before, fallbacks=)` |
| `desktop_manager/chrome/uia_core.py` | The UIA session `ChromeUia` builds on: reading windows and tabs, finding elements, menus and dialogs. | `UiaCore().list_windows()`, `.read_window(hwnd)`, `.find_tab_element(tab)`, `.select_tab(tab)` |
| `desktop_manager/chrome/window_info.py` | Chrome's top-level windows through Win32. | `chrome_window_handles()`, `window_profile(hwnd)`, `window_rect(hwnd)`, `monitor_origin(hwnd)` |
| `desktop_manager/chrome/input_windows.py` | Keyboard, foreground and polling helpers. | `press(key)`, `type_text(text)`, `bring_forward(hwnd)`, `poll(probe, timeout_seconds)` |
| `desktop_manager/chrome/pr_prune.py` | Pure PR planner, plus the protocol-typed executor that both planners share | `pr_ref_from_title`, `plan_prune(windows, resolution)`, `PrunePlan`, `apply_plans(plans, windows, actions)`, `keeper_preference` |
| `desktop_manager/chrome/site_rules.py` | Per-site title rules. Pure regexes with no Chrome dependency. | `identify(title) -> TabIdentity \| None`, `never_dedupe(title)`, `RULES`, `NEVER` |
| `desktop_manager/chrome/dedupe.py` | Pure planner for non-PR duplicate pages | `plan_dedupe(windows)`, `DedupePlan`, `site_tabs(windows)` |
| `desktop_manager/chrome/rank.py` | Pure tab ranking and memory-budget planner | `rank_tabs(windows, resolution)`, `RankedTab`, `plan_memory(ranked, limit_mb, already_closing=, held=)`, `MemoryPlan` |
| `desktop_manager/chrome/grouping.py` | Pure repo and site grouping planner | `repo_of(title)`, `site_of(title, host=)`, `plan_groups(windows, current_desktop=, min_size=, all_desktops=, hosts=)`, `GroupPlan`, `GroupMove`, `Ungroup` |
| `desktop_manager/chrome/saved_groups.py` | Pure saved-group chip parsing and consolidation planner | `parse_chip_name(name)`, `union_of_bars(bars)`, `plan_consolidation(groups)`, `ConsolidationPlan`, `SavedGroup` |
| `desktop_manager/chrome/history.py` | Read-only lookup of the hosts a title was visited on, from the profile's `History` database | `history_path(profile_dir)`, `hosts_by_title(profile_dir, titles)` |
| `desktop_manager/chrome/presence.py` | Keyboard and mouse idle time, and Chrome's microphone and camera use, for unattended runs | `idle_seconds()`, `InputWatch`, `devices_in_use()` |
| `desktop_manager/chrome/install.py` | The `chrome-tabs` command shim | `shim_path`, `runtime_desktop_manager`, `write_shim`, `find_checkout` |
| `desktop_manager/chrome/scheduled.py` | Entry point for the scheduled task (logs to `organise.log`) | `main()` |
| `desktop_manager/chrome/traces.py` | Organise-run trace files: where they go, retention, and the report | `start()`, `prune(dir)`, `prune_daily(dir)`, `load_spans(dir, days=)`, `runs(spans)`, `render_runs`, `render_tree`, `summarise` |
| `desktop_manager/chrome/cli.py` | The `chrome-tabs` Typer group | `chrome_tabs_app` |
| `desktop_manager/chrome/render.py` | The plans as printable lines | `render_plan(plan)`, `render_site_plan(plan)`, `render_rank(ranked)`, `render_memory_plan(plan)`, `render_group_plan(plan)`, `render_saved_groups(plan)` |
| `desktop_manager/chrome/cli_common.py` | Console, trace spans and window reads shared by the commands | `trace_span(name)`, `list_profile_windows(uia, profile, phase=...)`, `keep_desktop()` |
| `desktop_manager/chrome/group_run.py` | Carries out a grouping plan | `plan_grouping(uia, windows)`, `run_group_plan(uia, plan)` |
| `desktop_manager/chrome/saved_consolidation.py` | Deletes closed duplicate saved groups | `plan_saved_groups(uia, windows)`, `consolidate_saved_groups(uia, planned)` |
| `desktop_manager/chrome/unattended.py` | Unattended-run guards: calls, idle wait, input | `unattended_group_block(read_windows, idle_minutes)`, `live_call(windows)` |
| `desktop_manager/chrome/traces_cli.py` | The `chrome-tabs traces` Typer group | `traces_app` |

## How the adapter reads Chrome

- **Windows**: `EnumWindows` for class `Chrome_WidgetWin_1` with a non-empty
  title, owned by `chrome.exe`. This excludes Electron apps such as Slack,
  Docker Desktop and 1Password, which share the class. Minimised windows and
  windows on other virtual desktops are included, because UIA reads them too.
- **Profile**: taken from each window's shell property store. Its
  `RelaunchCommand` carries `--profile-directory`, and `RelaunchDisplayNameResource`
  carries the profile's display name.
- **Tabs**: a breadth-first search for the `TabStripView` element. The search
  never descends into web content (`Document` elements), so a 60-tab window
  takes under a second. It finds `TabView` children below that element. Tab
  names include Chrome's suffixes, such as `- Memory usage - 346 MB` (present
  only on loaded tabs) and alerts such as `- Audio playing`.
  `parse_tab_name` strips these suffixes.
- **Window age**: Chrome assigns accessibility ids monotonically across the
  process, so the root view's RuntimeId orders windows by creation. The
  "leftmost tab in the oldest window" rule relies on this ordering.
- **Closing**: the adapter first re-finds the tab by RuntimeId and checks its
  title. It then selects the tab, waits for its `TabCloseButton` (Chrome shows
  the button on the active tab), and invokes the button. The invoke happens
  only if the tab is still selected and the button is named `Close`. Finally
  the adapter polls until the tab is gone. Every close re-finds its target, so
  index shifts cannot redirect a close. `restore_selection` then puts each
  window's active tab back. If the active tab was closed, it activates the
  planned keeper, or else leaves Chrome's own choice. Closing the last tab of a
  group makes Chrome ask "Close tab and delete group?"; the adapter confirms
  "Delete group".
- **Groups**: a grouped tab's raw-view parent is a `TabGroupView`, whose
  `TabGroupHeaderView` is named `group <title> – N tabs` (or
  `unnamed group – …`). Right-clicking the header opens the group editor
  bubble, whose entries (`Ungroup`, `Move group to new window`, `Delete group`
  and so on) are `BubbleMenuItemButton`s, matched by exact name.
- **Dialogs**: Chrome's confirmations ("Ungroup tab group?") are tab-modal
  child widgets of the browser window, found in its UIA subtree rather than
  among top-level windows.

## PR rules

The rules run per profile, across all of that profile's windows. A tab is
identified by its title (`… · Pull Request #n · owner/repo`), keyed as
lowercase `owner/repo#n`. `gh api graphql` resolves every PR, using one aliased
query per repo, and also fetches that repo's full open-PR list for chain
building.

| Rule | Action | Reason shown |
|---|---|---|
| Merged | every tab closes | `merged` |
| Closed without merging | every tab closes | `closed without merging` |
| Stack (X.base == open Y.head) | only the lowest member that has a tab keeps it | `superseded by #N (stack bottom)` |
| Queue: native `mergeQueueEntry` by position, or `locked-merging` then `automerge` by label time | only the front member that has a tab keeps it | `queued behind #N` |
| Duplicate | one tab kept: active, else loaded, else leftmost in the oldest window | `duplicate of tab i in window w` |
| Unknown state (resolver error) | report only; never closed, even as a duplicate | |

Pinned and audible tabs are never closed. They appear under the report heading
together with the rule that would have closed them. If a repo's open-PR list
fails to load, the chain rules are skipped for that repo.

## Site rules

UIA exposes a tab's title but never its URL. So two non-PR tabs count as "the
same page" only when a rule says their titles name the same resource. The rules
were derived from Chrome history (September 2026) for the sites used daily.
They are matched against the title after Chrome's suffixes have been stripped.
Keys are compared per profile, across that profile's windows. The keeper
choice matches the PR duplicate rule: the active tab, else a loaded one, else
the leftmost in the oldest window.

Safety layers, applied in order:

1. **`NEVER`**: titles that name no specific resource are never keyed, and so
   are never closed. These include the claude.ai shells (`Claude`,
   `Claude Code`, `New chat - Claude`); sign-in, redirect and 404 pages; Google
   Meet (a tab may be a live call, and meeting names repeat daily); Gmail and
   Drive home pages; Google searches; app shells such as Vercel, Slack, Kibana,
   Asana and Miro; AWS list pages (the account is never in the title); Rights
   App pages (LIVE, clone-N and localhost share titles); locally generated
   `Datalake schema` reports; and tabs showing a URL or nothing.
2. **`exact_only`**: claude.ai titles an artifact either `X` or `X - Claude`.
   Both forms are keyed as `title:X`, but a tab closes only when its title
   exactly matches that of another tab. A cross-variant match appears under
   *title variant of a kept tab* instead. The reason is that a cowork session,
   and a login redirect partway through, can both borrow an artifact's name.
3. **Report-only rules** (`auto=False`): never closed, only listed.
4. **Different URLs**: where the platform reports each tab's URL (macOS does;
   UI Automation does not), a title match whose scheme, host or path differ is
   listed under *same title, different URL* and never closed. The query and
   fragment do not split a duplicate. If either URL is unknown, the title
   decides, as before.

| Rule | Title shape | Key |
|---|---|---|
| github-issue | `… · Issue #n · owner/repo` | `gh-issue:owner/repo#n` |
| github-file | `path at ref · owner/repo` | `gh-blob:owner/repo@ref:path` |
| github-list | `Pull requests · owner/repo` (Issues, Actions, Commits, Releases, Branches) | `gh-list:owner/repo:kind` |
| github-run | `… · owner/repo@sha`: report only, because re-runs share a title | `gh-run:…` |
| youtrack | `… : PROJ-123` | `youtrack:PROJ-123` |
| jira | `[RA-123] … - JIRA` | `jira:RA-123` |
| gmail | `Inbox (13) - me@x - … Mail`, unread count dropped | `gmail:me@x:Inbox` |
| google-calendar | `acct – Calendar - view` | `gcal:acct:view` |
| google-docs | `T - Google Docs` (Sheets, Slides, Forms) | `gdoc:kind:T` |
| google-drive | `T - Google Drive` | `gdrive:T` |
| asana | `● T - Asana`, unread dot dropped | `asana:T` |
| aws | `res - … Details \| service \| region` | `aws:region:service:res` |
| nuget | `NuGet Gallery \| Pkg 1.2.3` | `nuget:Pkg 1.2.3` |
| claude | `X - Claude` (`exact_only`) | `title:X` |
| title | any multi-word title, including a bare artifact title | `title:<title>` |
| title | a one-word title: report only | `title:<title>` |

The first matching rule wins. To add a site, put a rule in `site_rules.RULES`
before the `title` fallbacks, or a pattern in `NEVER`. Add real titles for the
new site to `tests/desktop_manager/test_chrome_dedupe.py`.

## Ranking and the memory budget

`rank_tabs` puts each tab in one of two groups. A **protected** tab is never
closed by the memory budget. Tabs are protected, in this order, for being:

1. a video call, matched by title (see `site_rules.CALLS`);
2. audible, or using live media;
3. pinned;
4. the active tab of its window;
5. a page that `site_rules` cannot identify.

Every other tab gets a **score** built from named factors:

| Factor | Score |
|---|---|
| your review is requested | +3 |
| your own open PR | +2 |
| ... with failing checks, or with changes requested | +1 each |
| a YouTrack/Jira ticket, a GitHub issue, or a Google doc | +1 |
| someone else's approved PR that isn't waiting on you | -1 |
| someone else's draft PR | -1 |
| a closed-unmerged PR | -1 |
| a merged PR | -2 |
| a weakly identified title (one word, or an Actions run) | -1 |
| in a minimised window | -1 |

The size, draft, review decision, head-commit check rollup, authorship and
review requests all come from the same per-repo GraphQL lookup that reads PR
state (`PrState.details`). PRs with more than 800 changed lines are flagged as
`oversized`. The flag appears in the output but does not change the score.

`--max-memory` totals the memory reported by tabs that are still open after the
PR and site plans. It then closes unprotected loaded tabs until the total fits:
lowest score first, and the biggest first within a score. A tab that another
plan listed for you to decide (a title variant, a report-only match, a
protected duplicate) still counts toward the total but is never closed. The
budget covers the per-tab footprints Chrome prints in its tab names. It does
not include Chrome's browser, GPU and extension processes, so Task Manager will
show a higher figure. Discarded tabs report nothing and cost nothing.

Chrome can't reorder tabs through UI Automation, and injected Ctrl+W and
similar shortcuts don't reach it. That rules out physically sorting the tab
strip, so `rank` prints the order instead.

Call titles: Meet's (`Meet – <meeting>`, `Google Meet`, `Meet`) come from
history. The patterns for Zoom, Teams, Slack huddles, Webex, Jitsi and Whereby
use each service's documented web-client titles and have not been seen live.

## Limits

- **Titles only.** A PR tab whose title was never loaded or is stale still
  keys correctly, because the number does not change. Other pages are only as
  reliable as their titles: two different pages that share a multi-word title
  are treated as duplicates. `NEVER` exists to exclude the sites where this
  happens. Check `--dry-run` after adding a site.
- **English UI.** The suffixes and the `Close` button name are English.
  Chrome's view class names (`TabStripView`, `TabView`, `TabCloseButton`) are
  internal and may change between Chrome releases. When that happens the
  adapter finds nothing, or refuses to close; it never clicks the wrong
  control.
- **Discarded tabs** reload when they are selected, just before they close.
- **Pinned detection** checks for a `Pinned*` ancestor view. It has not been
  exercised live against a pinned tab.
