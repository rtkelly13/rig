# Orca

Orca (`/Applications/Orca.app`, CLI `orca`) runs the agent terminals and worktrees for the
personal estate. `config/orca.json` declares how it should be set up; `workspace orca
doctor` checks the install against it, and `workspace orca sync` remediates and applies
per-repo configuration.

```bash
uv run workspace orca doctor          # table; exits 1 on any failure
uv run workspace orca doctor --json

uv run workspace orca sync            # dry-run: preview changes against Orca
uv run workspace orca sync --apply    # apply planned changes
uv run workspace orca sync --apply --repo rig  # target a specific repo

# a second Orca instance, such as the personal one on a work machine, from outside it
uv run workspace orca sync --orca-data "$APPDATA/orca-personal" --root C:/code --apply
```

Both commands take `--orca-data <folder>`, an Orca instance's user-data folder. Without it they
use the Orca whose terminal they run in (Orca sets `ORCA_USER_DATA_PATH` there), and outside Orca
the platform default (`~/Library/Application Support/orca`, `%APPDATA%/orca`, `~/.config/orca`). With it, the profile, `orca-runtime.json` and token come from that folder and the
`orca` CLI is pointed at it through `ORCA_USER_DATA_PATH`. A terminal Orca launched is bound to that
instance, so pass the folder of the instance you are running in or the CLI reports
`runtime_source_mismatch`.

Repo paths in the policy are written `~/code/<group>/<name>`. They resolve under this machine's
code root (`rig.roots`, `C:/code` on the Windows work machine), not literally under the home
folder. `--root` is the personal folder, for repos the policy names without a path.

The [job coordinator](desktop-manager/jobs.md) runs `sync --apply` and `estate orca --apply`
daily against the personal instance (`orca-personal` on Windows).

## What `config/orca.json` declares

| Key | Meaning |
|---|---|
| `agent_args` | Each agent's custom launch arguments (Settings → Agents, stored as `agentDefaultArgs`). `""` means none. Agents left out are not checked. Never put a permission flag here: that is what `agent_permissions` is for. |
| `agent_permissions` | Each agent's permission mode (Settings → Agents): `own`, `ask`, `accept-edits`, `auto` or `full-access`. `default` covers agents not named in `agents`. See [Agent permissions](#agent-permissions). |
| `workspace_dir` | Where Orca creates its worktrees (`~` is expanded). |
| `agent_sleep` | Orca's experimental Agent sleep (`experimentalAgentHibernation`, `agentHibernationIdleMs`): `{"enabled": true, "idle_minutes": 30}`. Orca stops an agent terminal once its turn has been done for the idle window, and resumes it (`claude --resume <id>`) when the tab is opened again. The tab on screen, agents with running sub-agents and unsent drafts are never stopped. See [Agent sleep](#agent-sleep). |
| `repos.<name>.path` | Optional filesystem path to repo (defaults to `<estate_root>/<name>`). |
| `repos.<name>.setup` | The setup script each new Orca worktree should run. The repo's committed `orca.yaml` should declare it (see below). |
| `repos.<name>.archive` | The archive script, run when a worktree is archived; same rule as `setup`. |
| `repos.<name>.external_worktree_visibility` | `show`, `hide` or `inherit`: whether worktrees Orca did not create appear in the sidebar. |
| `repos.<name>.base_ref` | Default base ref for future worktrees (e.g. `main`). |
| `repos.<name>.worktree_base_path` | Orca's per-repo Worktree Location. Relative paths resolve from the repo root; `""` means the global workspace folder. |
| `repos.<name>.color` | Hex badge color (e.g. `#6366f1`). |
| `repos.<name>.icon` | One of the lucide names Orca's icon picker offers (e.g. `SquareTerminal`, `Palette`), an emoji (`emoji:🛠️`), or an image URL. See [Repo icons](#repo-icons). |

Repo names are Orca display names, which match the `managed-projects.json` names. The
doctor also warns about any managed project cloned under the estate root but not
registered in Orca.

Current policy: Claude and Claude agent teams launch in **Auto** (`--permission-mode auto`), never
with `--dangerously-skip-permissions`, and Codex in **Auto** (`--approve-for-me`). OpenCode 2
(`opencode2`) launches with its own `--auto`, as a custom argument, since Orca offers OpenCode no
permission mode. Antigravity (`agy`) runs in **Full access**. The default for every other agent is
**Own**, so Orca adds no flags to them. The original OpenCode, Copilot and Muse are not in the
policy: they are uninstalled, so Orca does not detect them.

The rule behind it: no constant prompting, and nothing run dangerously. An agent uses its own
auto mode where it has a workable one: Claude Code's auto mode keeps the allow and deny rules that
`rig policy sync` projects, and OpenCode has `--auto`. Full access is only for a tool that
prompts on every step and has no workable auto mode, as Antigravity does.

## Where the doctor reads from

- **Launch arguments**: live from the running app through `settings.get`, which serves each
  agent's whole launch line (permission flags plus custom arguments). Skipped when Orca is not running.
- **Settings**, including custom arguments and permission modes: Orca's `profile-state.db` in its
  profile folder (`<user-data>/profiles/local-default`; for the default instance `ORCA_PROFILE_DIR`
  overrides it), opened read-only. Orca holds it open and rewrites it, so rig never writes to it.
  When it cannot be read, the newest `profile-state.db.backup.*.db` beside it is used instead.
  The `source` row names which, with its age, and warns past 24 hours.
- **Repos**: `orca repo list --json`, which is safe while Orca runs.

## Repo icons

Orca draws only the lucide icons its picker offers, matched by exact name: `Folder`, `Code2`,
`SquareTerminal`, `Bot`, `Package`, `Database`, `Globe`, `Server`, `Cpu`, `Layers`, `Braces`,
`Rocket`, `Wrench`, `Briefcase`, `Building2`, `Palette`, `Gauge`, `Sparkles`, `Shapes`, `Box`.
Any other name shows as a folder, and `repo.update` ignores names that are not alphanumeric (such
as `git-compare`) but still reports success. For anything else, use `emoji:` (up to 16 characters)
or an image URL. The doctor fails an icon outside that set, and a test holds `config/orca.json`
to it. `OrcaRepoIcon.Lucide` in `src/Rig.Core/Orca/OrcaState.cs` mirrors the list; update it when Orca's
picker changes.

## Worktrees under each repo's `temp/`

Every managed repo gitignores `temp/`, and `worktree_base_path: "temp"` makes Orca create new
worktrees there instead of under `workspace_dir`. Orca's "nest workspaces" setting (on here)
still adds the repo's folder name, so a new rig worktree lands at
`~/code/personal/rig/temp/rig/<name>`; with nesting off it is `temp/<name>`. That setting is
global and only changes in Orca's Settings. Existing worktrees stay where they are.

## Setup and archive scripts belong in `orca.yaml`

Orca reads `orca.yaml` from the root of each new worktree:

```yaml
scripts:
  setup: |
    set -e
    mkdir -p temp
    [ -e TODO.md ] || printf '# TODO: %s\n\n- [ ] Local tasks and notes (gitignored)\n' "$(git branch --show-current)" > TODO.md
    uv sync && pnpm install
```

Each managed repo's setup starts with `set -e`, so the first failure fails the setup,
then the same two lines: they create the gitignored `temp/`
scratch folder and a gitignored `TODO.md` naming the branch, then install dependencies.

Orca also keeps its own local script per repo, in its per-repo settings.
`commandSourcePolicy` chooses between them, and when it is unset **any local script wins and
`orca.yaml` is ignored**. So the doctor reports a script as:

| Status | Meaning |
|---|---|
| OK | `orca.yaml` declares it and Orca uses it |
| WARN | it runs, but only from Orca's local settings, or a local copy overrides `orca.yaml` |
| FAIL | a new worktree would run something else, or nothing |

Because `orca.yaml` is read from the worktree, it takes effect once it is committed on the
base branch. The doctor reads `orca.yaml` from the repo's checkout at its registered path, so a
file there that is not yet committed reads OK while new worktrees will not get it. `sync` never writes repo files and never copies scripts from
`config/orca.json` into Orca; add `orca.yaml` to a repo through a PR.

## Fixing what it finds: `sync`

`workspace orca sync` plans changes and makes them with `--apply`. It sends only the fields
that differ:

1. **Unregistered repos**: `orca repo add --path <path>`. Run `sync` again for their settings.
2. **External worktrees and base ref**: `orca repo set` and `orca repo set-base-ref`.
3. **Badge colour, icon and worktree location**: no CLI command exists, so these use `repo.update`, the call
   Orca's own CLI makes over the running app's Unix socket, or its named pipe on Windows,
   authenticated with the token in `orca-runtime.json`. It is not a documented interface. It needs
   Orca running; when the socket or pipe refuses or is unreachable the row is an error, not a silent
   success.
4. **Local scripts shadowing `orca.yaml`**: cleared through `repo.update`, and
   `commandSourcePolicy` set to `shared-only`. Orca replaces `hookSettings` whole, so the
   other hook settings are read first and sent back unchanged. A local script with no
   `orca.yaml` equivalent is never cleared; that row is `manual`.
5. **Scripts `orca.yaml` lacks**: `manual`, with the script to commit.
6. **Agent launch lines** (permission mode and custom arguments): sent through `settings.update`
   for each policy agent whose live line differs. Skipped with `--repo`. See
   [Agent permissions](#agent-permissions) for how Orca splits a line.
7. **Workspace folder**: change it in Orca's Settings.
8. **Agent sleep**: `manual`, naming the change to make under Settings → Experimental.

### Agent permissions

Orca 1.4.255 split an agent's launch line into a **permission mode** and **custom arguments**. The
upgrade folded the old per-agent flags into a single **Full access** default
(`agentPermissionDefault`, with `agentPermissionOverrides` per agent) and emptied the custom
arguments. From then on Claude launched with `--dangerously-skip-permissions`, although
`config/orca.json` still read correctly.

The flags each mode adds come from Orca's `tui-agent-permissions.ts`. Only Claude, Claude
agent teams and Codex have an Auto mode:

| Mode | claude, claude-agent-teams | codex | others |
|---|---|---|---|
| Own | none | none | none |
| Ask | `--permission-mode default` | `-a on-request -s read-only -c approvals_reviewer=user` | varies |
| Accept edits | `--permission-mode acceptEdits` | — | — |
| Auto | `--permission-mode auto` | `--approve-for-me` | — |
| Full access | `--dangerously-skip-permissions` | `--dangerously-bypass-approvals-and-sandbox` | e.g. `--yolo` (copilot, gemini) |

`doctor` checks three things:

- **permission**: the default mode and each agent's mode, as stored. An agent's override wins
  over the default, and an override the policy does not expect fails. Agents Orca offers no mode
  for (OpenCode, OpenCode 2, omp: not in its flag tables) are not checked: Orca can keep an
  override for them, such as the Full access the upgrade left on OpenCode, but launches them the
  same in every mode and shows no choice in Settings.
- **custom args**: the stored `agentDefaultArgs`.
- **launch args**: what the running Orca would launch the agent with, against its mode's flags
  plus its custom arguments, compared as sets of tokens. An agent and mode not in the table above
  is not checked.

`settings.update` rejects `agentPermissionOverrides` (`Unrecognized key`), but it takes whole
launch lines in `agentDefaultArgs` and splits each again, as the Settings field does: a mode's flags
set that agent's mode and the rest become its custom arguments. A changed line with no flags sets
**Own**; an unchanged one changes nothing. So `sync` sends each policy agent's composed line,
merged onto the live map (Orca replaces the map whole), and reads back what Orca then serves. A
line Orca serves differently is an error naming the Settings change instead. That happens moving a
flagless agent with unchanged arguments back to Own, since Orca keeps its mode.

Send only lines rig composed from the policy. Before Orca 1.4.255 recorded `agentLaunchChoices`,
a line with arguments it could not split left them "mixed", and Orca refused to change that agent's
mode until they were edited by hand ("Edit custom launch arguments before choosing a permission
mode").

A sleeping agent keeps the launch line it started with, in the session's saved launch config, and
resuming it reuses that line. After changing a mode, start the agent's session again rather than
resuming it.

### Agent sleep

Every idle Claude session keeps 300–500 MB resident. On an 8 GB Mac, two dozen idle sessions pushed
the machine deep into swap while CPU sat mostly free, so the policy turns Agent sleep on. Sleeping
stops the terminal; the worktree and the session are kept, and opening the tab resumes it.

Neither half can be set by rig while Orca runs: `settings.update` rejects the keys
(`Unrecognized key: "experimentalAgentHibernation"`, Orca 1.4.250), and Orca's database is never
edited under it. So `doctor` checks it from the newest settings backup, and `sync` reports a
`manual` row. A backup can trail a Settings change by up to a day; the `source` row says how old it is.

A sleeping session has no live terminal, so [`estate wrapup`](estate/wrapup.md) shows it as
`no-terminal` and cannot ask it. Run wrap-up inside the idle window, or open the tab first.

## Restoring agent sessions into Orca

Open a session as a terminal tab in the worktree it belongs to:

```bash
orca terminal create --worktree path:<dir> --title "<name>" --command "<resume command>"
```

The command is typed as given, so it bypasses `agentDefaultArgs`.

| Agent | Resume command | Folder-scoped? |
|---|---|---|
| Claude | `claude --resume <id>` | Apparently not: `--resume <id> --fork-session` from another folder loaded the session and saved under the new folder (Claude Code 2.1.310); plain `--resume` should match. Claude memory is per repo, so copy notes when moving sessions out of `~/code/personal`. |
| Antigravity | `agy --conversation <uuid> --dangerously-skip-permissions` | No. The conversation still records its old folder, so tell it where it now is. |
| OpenCode | `opencode --auto -s <id>` | **Yes.** `-s` creates an empty session when the id is not in the current folder's project, so launch from the original folder or start fresh with a recap. |

`desktop-manager claude-sessions list --json` lists recent Claude sessions with where each
was last working. Warp's history of agent launches and their folders is in the `commands`
table of `warp.sqlite` under `~/Library/Group Containers/*.dev.warp/`.

A first launch in a new folder stops at the agent's "trust this folder" prompt; answer it
in the tab.

## Updates

On the Work desktop two Orca profiles run side by side: `Work Orca` on the default profile and
`Personal Orca` on `--user-data-dir=%APPDATA%\orca-personal`. Orca's in-app "restart to update"
runs the NSIS installer, which closes *every* `Orca.exe` but relaunches only the profile that
asked. rig applies updates instead, so dismiss Orca's update prompts.

- electron-updater stages each download in `%LOCALAPPDATA%\orca-updater\pending\`
  (`update-info.json` and `orca-windows-setup.exe`) and never clears it after installing. An update
  is pending only when the installer's file version is newer than the installed build (the
  per-user Uninstall entry's `DisplayVersion`).
- **At restore**, before any window is inventoried, a pending update is installed silently
  (`--updated /S`, after checking the recorded SHA-512) when no Orca is running. The restore then
  launches both profiles as usual. With Orca already running the update stays staged and the run
  log points at the command below.
- **On demand**:

  ```bash
  uv run desktop-manager workspace update-orca --config <plan> [--dry-run] [--close-timeout 60]
  ```

  It asks every Orca window to close and waits (Orca saves sessions as it quits) before
  force-killing survivors, waits out any installer Orca started itself on quit, installs if still
  behind, then relaunches every enabled Orca entry in its planned placement, Work before Personal.
  It exits 0 with nothing to do when Orca is current, and 1 when the installer failed (Orca is
  still relaunched on the old build).
- If an update was applied from inside Orca anyway, `restart-app --name "Personal Orca"` or the
  next restore brings the missing profile back.

## Agent tabs closed by a Windows shutdown

**Symptom.** After a restart, some or all of an instance's agent tabs are gone: the worktrees
are still in the sidebar but their Claude tabs are not, or only a fresh `pwsh` tab is left. One
instance can lose everything while the other keeps most of its tabs.

**Cause** (Orca 1.4.247, Windows). At sign-out Windows kills the console processes under
Orca's terminal daemon before Orca's window is told the session is ending. The daemon reports
each one as `session-exited` with `cause: "exited"` (often code `1073807364`, `0x40010004`),
the still-running window takes that as the shell ending, closes the tab and saves. The
`system_session_end` notice arrives a second or two later. Whichever instance hears about more
exits first loses more tabs; it is a race, not a profile setting. This is Orca's bug, and
nothing in rig causes or prevents it.

**Evidence**, all under the instance's user-data folder (`%APPDATA%/orca` for work,
`%APPDATA%/orca-personal` for personal):

- `logs/daemon.log`: a burst of `session-exited` / `"cause":"exited"` at shutdown, before the
  next `startup`.
- `logs/main.trace.ndjson`: the `system_session_end` breadcrumb (`reasons: shutdown`) after
  that burst.
- `profiles/local-default/profile-state.db`, domain `workspaceSession`:
  `activeWorktreeIdsOnShutdown` and `unifiedTabs` shrink compared with the newest
  `profile-state.db.backup.*.db` from before the restart.

**Nothing is lost for good.** The transcripts stay in `~/.claude/projects`, and the backup
still records each tab's worktree, title and agent session id (`unifiedTabs[].aiVaultTitle`,
plus `sleepingAgentSessionsByPaneKey[].providerSession` with the session's own `cwd`).

**Recover** with the `claude-session-recover` skill's Orca script, which is read-only:

```bash
uv run python -I skills/claude-session-recover/scripts/orca_lost_tabs.py --orca-data "$APPDATA/orca"
```

It compares the newest backup saved before the running Orca started with the live state, skips
sessions already open again, and prints an `orca terminal create` line per lost agent tab. Each
line reopens the tab in the worktree it sat in. When the agent session ran in another folder,
the command `cd`s there first: `--worktree path:` only accepts worktrees Orca has registered,
and a session started in a worktree the sidebar hides fails with `selector_not_found`.

**Recover the same day.** Orca writes a backup about once an hour and keeps only the newest
five, so the pre-restart snapshot rotates away roughly five hours after Orca comes back up.
Once it has, the script stops with `no backup saved before this Orca started`, and the
fallback is `daemon.log` (`session-exited` at the shutdown time) plus
`desktop-manager claude-sessions list`.

A tab opened after the newest pre-restart backup is not in it. Check `daemon.log` for
`session-created` events after the backup's time, and `desktop-manager claude-sessions list`
for their sessions.

### Driving the other instance's CLI

A terminal Orca launched is bound to it, so pointing `ORCA_USER_DATA_PATH` at the other
instance is refused with `runtime_source_mismatch`. Clear the binding for that one call:

```bash
env -u ORCA_RUNTIME_SOURCE_ID ORCA_USER_DATA_PATH="$APPDATA/orca" orca repo list --json
```

Write paths with forward slashes in `--command`: it is typed into `pwsh`, and backslashes
picked up shell escaping on the way.
