# Tool caches

**Up:** [`estate`](README.md) · [Worktrees](./worktrees.md) · [Shelving](./shelving.md) · [Decision feed](./decisions.md) · [Orca](./orca.md) · [Internals](./internals.md) · [Lessons](./lessons.md)

`estate prune-caches` and the scheduled job that runs it.

`estate prune-caches` prunes the caches each tool can rebuild, and reports the
bytes each category gave back. The scheduled `estate-prune-caches` job runs it
daily with `--apply`.

| category | command | measured by |
|---|---|---|
| uv cache | `uv cache prune` | walking `uv cache dir` before and after |
| pnpm store | `pnpm store prune` | walking `pnpm store path` before and after |
| nuget http-cache | `dotnet nuget locals http-cache --clear` | walking the listed http-cache |
| claude temp | deletes `<temp>/claude/<project>/<session-id>` dirs idle `--idle-days` (7) | the dirs themselves |
| docker build cache | `docker builder prune -f --filter until=168h` | Docker's own `Total:` line |

Every tool is optional. A tool that cannot be found is `skipped` with that
reason, and so is Docker when its daemon does not answer `docker info`. A
scheduled task starts with a thinner PATH than a terminal, so each tool is also
looked for where its installer puts it (`~/.local/bin`, WinGet `Links`,
`%LOCALAPPDATA%\pnpm`, `%APPDATA%\npm`, `Program Files\dotnet`, Docker's
`resources\bin`).

A dry run runs only the read-only commands that locate each cache, so its
`frees` column is filled in only where that is knowable without pruning: the
NuGet http-cache is cleared outright, and the idle Claude sessions are listed
exactly. A Claude session counts as idle when neither its newest file nor the
directory itself has been written for `--idle-days`. `--json` prints the
results; the command exits 1 when any category failed.

The categories, the tool lookup and the idle-session rule live in
`src/Rig.Core/Estate/Caches.cs`; the table and `--json` in
`src/Rig.Cli/Estate/PruneCachesCommand.cs`.
