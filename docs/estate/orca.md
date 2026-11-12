# Orca's sidebar

**Up:** [`estate`](README.md) · [Worktrees](./worktrees.md) · [Shelving](./shelving.md) · [Caches](./caches.md) · [Decision feed](./decisions.md) · [Internals](./internals.md) · [Lessons](./lessons.md)

`estate orca`: Orca's projects and groups, managed from `config/managed-projects.json`.

`config/managed-projects.json` is the one place that says how the personal
estate is organised. Orca shows it. Each project that belongs in Orca carries
its group:

```json
{ "name": "blobflags", "repo": "git@github.com:rtkelly13/blobflags.git", "orca": { "group": "personal/tooling" } }
```

The group is a `/`-joined path of Orca group names from the top. Top-level
groups are Orca's folder-scan groups (`personal`, `rtkelly-labs`, `Resultful`),
and subgroups such as `personal/tooling` are made by hand in the sidebar.
`"group": ""` means the top level, in no group (`rig`, where most experiments
start). A project without `orca` is not meant to be in Orca (reference clones,
disabled entries).

`estate orca` compares that with Orca. `--apply` closes what it can:

| kind | means | `--apply` |
|---|---|---|
| `unregistered` | declared and cloned, not in Orca | registers it, then moves it |
| `wrong-group` | in Orca under another group, or none | moves it |
| `missing-group` | a declared group Orca does not have | creates it, parents first |
| `undeclared` | in Orca, but its entry has no `orca.group` | nothing: add one |
| `unmanaged` | an estate repo in Orca that managed-projects.json does not list | nothing: add it, or remove it from Orca |
| `stale` | registered, but the folder is gone | nothing |
| `no-clone` | declared, but not cloned where its top group's folder says | nothing: `git clone`, then run again |
| `archived` | in Orca, but its repo is archived | removes it from Orca, unless a process is working in it |
| `archived-clone` | an archived repo is still cloned locally | nothing: delete the clone once nothing exists only there |
| `empty-group` | a group with no project or folder workspace below it, and no declared project in it | deletes it |
| `in-work-profile` | an estate repo in the work Orca profile (Windows) | removes it from the work profile |
| `work-empty-group` | a work-profile group that holds only estate repos | deletes it once they are removed |
| `skipped` | in Orca, but this machine's `tools.estate.skip_repos` lists it | nothing: remove it from Orca by hand |

**Archived repos never belong in Orca.** Each run asks GitHub, in one GraphQL
query, which of the listed and registered estate repos are archived. Retired
projects (archived or deleted) move out of `managed-projects.json` into
`config/archived-projects.json`, which the run also reads, so a clone or
registration left behind is still reported. An archived repo is never reported
as `unregistered` or `no-clone`. When GitHub can't be asked, the run says so and
only `archived-projects.json` counts.

Removing a project from Orca (`repo.rm`) only unregisters it and stops its
terminals; Orca deletes no files. `--apply` still skips any archived project a
process is working in (`lsof -d cwd`). Deleting an archived clone is left to a
human, because it can hold the only copy of uncommitted work or commits no
remote has; `estate risk` shows what. A **deleted** repo (GitHub returns nothing)
is not archived, and its clone may be the only copy anywhere, so nothing here
touches it.

**A machine can skip managed repos.** `~/.workspace/config.json` (or
`config.json` in `WORKSPACE_HOME`) is per machine. Managed-project names listed
under `tools.estate.skip_repos` are never cloned or registered on that machine,
while every other machine still gets them from `managed-projects.json`:

```json
{ "tools": { "estate": { "skip_repos": ["hearth"] } } }
```

`estate orca` never reports a skipped repo as `unregistered` or `no-clone`, and
`--apply` neither registers it nor creates its group. If it is already in Orca,
`--apply` leaves it alone (no move, no removal) and the run reports it as
`skipped`. The workspace audit never reports it as a missing clone. To keep a
repo off every machine, set `"enabled": false` in `managed-projects.json`
instead.

**Groups are deleted, not just created.** A group is kept while any live
declared project sits in it or below it. Anything else with no project and no
folder workspace below it is deleted, outermost first. Orca deletes a group's
subgroups with it, moves any projects to the top level and drops its folder
workspaces, so a group that still holds a folder workspace is never deleted.

**On Windows the estate has its own Orca profile.** The work desktop runs two
Orca windows: the default profile (`%APPDATA%\orca`) for work, and
`%APPDATA%\orca-personal` (`--user-data-dir`) for the estate. `estate orca`
manages `orca-personal` there (`--orca-data` overrides it), and also checks the
work profile (`--work-orca-data`). Every `rtkelly13`, `rtkelly-labs` and
`Resultful` repo is reported there as `in-work-profile`, declared or not, and
`--apply` removes them and deletes the groups they leave empty. When the work
profile isn't running, the run warns and carries on. On macOS and Linux both are
the one default profile, and there is no work-profile check.

Top-level projects move last, so they land at the bottom of the top level.
Only `rtkelly13`, `rtkelly-labs` and `Resultful` repos count. Work repos keep
their hand-made Orca groups, and the audit never mentions them.

**It talks to the running Orca app, not its CLI or its files.** The `orca` CLI
can register a repo, but it cannot list groups or move a project into one, and
`orca repo add` leaves a project in no group. The app's runtime API, which the
CLI and Orca's mobile app both use, has all of it: `repo.list`, `repo.add`,
`projectGroup.list`, `projectGroup.create`, `projectGroup.moveProject`,
`projectGroup.delete`, `folderWorkspace.list` and `repo.rm`.

- The command runs on the C# CLI (`src/Rig.Cli/Estate/OrcaLayoutCommand.cs`, logic in
  `src/Rig.Core/Estate/OrcaLayout.cs`). `OrcaRuntime` (`src/Rig.Core/Orca/`) reads
  `orca-runtime.json` from Orca's user-data folder (`--orca-data` and `--work-orca-data`
  override it) for the socket and auth token.
- Each request is one JSON line over that socket, as in stablyai/orca
  `src/cli/runtime/transport.ts`.
- Orca makes the change and saves it. Nothing here writes to Orca's
  `profile-state.db`.

Those methods are Orca internals, not a published API. If a release changes
them, or Orca is not running, the command fails with exit code 3 rather than guessing. When that
happens, read the matching tag of the Orca source:
`git clone https://github.com/stablyai/orca ~/code/external/orca`.
