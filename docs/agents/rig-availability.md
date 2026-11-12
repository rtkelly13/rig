# Global Tooling: Rig Availability

`rig` is the personal estate CLI and developer platform installed globally across developer machines (macOS and Windows).

AI agents working in any repository across `~/code/personal` or organizational folders (`rtkelly-labs`, `Resultful`) have access to `rig` directly on `PATH`.

______________________________________________________________________

## Capabilities Overview

`rig` provides specialized, cross-repository capabilities designed to streamline agent workflows, enforce repository hygiene, and eliminate ad-hoc shell scripting.

### 1. PR Lifecycle & Landing (`rig gh pr`)

Avoid manual shell polling loops (such as looping `gh pr view` or `gh pr checks --watch`), which hang indefinitely on unresolved review threads or merge-queue stalls:

- **`rig gh pr land <PR> [--repo owner/name]`**: Queues squash auto-merge (`gh pr merge --auto --squash`) and waits until landed or blocked.
- **`rig gh pr wait <PR> [--repo owner/name] [--timeout 60m] [--settle 30s] [--json]`**: Waits for an already queued PR to merge. Features exponential backoff (5s–60s) and settle confirmation.
- **Exit Codes**:
  - `0`: Merged.
  - `1`: Blocked (`check_failed`, `unresolved_threads`, `changes_requested`, `review_required`, `conflicts`, `draft`, `closed`, `auto_merge_off`, `removed_from_merge_queue`).
  - `2`: GitHub API / network failure.
  - `3`: Timeout.
- See [docs/git/waiting-for-a-pr.md](../git/waiting-for-a-pr.md).

### 2. Review Thread Resolution (`rig gh threads`)

- **`rig gh threads list <PR> [--repo owner/name]`**: Lists open review conversations and their IDs.
- **`rig gh threads resolve <PR> <THREAD_ID> --sha <SHA> [--note "..."] [--repo owner/name]`**: Truthfully replies and resolves review threads against committed SHAs.

### 3. Estate & Workspace Management (`rig estate`, `rig workspace`)

- **`rig status`**: Estate-wide health overview: uncommitted changes, branches, lint, type-checks, and dirty worktrees.
- **`rig estate orca-worktrees`**: Audits, keeps, or removes finished/orphaned agent worktrees without losing unpushed work.
- **`rig estate tidy --repo <path> --apply [--merged]`** (or **`--all --apply`**): Safely deletes local feature branches whose work has already merged (dry run by default without `--apply`; pass `--merged` to prune merged branches).
- **`rig workspace sync`**: Aligns estate workspace configurations and indexes.

### 4. Repository Governance & Linting (`rig repo-governance`, `rig md-lint`)

- **`rig repo-governance agents-audit`**: Validates `AGENTS.md` instruction budgets, link integrity, and formatting across repos.
- **`rig md-lint <paths> [--fix]`**: Lints or auto-fixes Markdown formatting to ensure compliance with estate style.

### 5. Environment & Tooling Orchestration (`rig devenv`, `rig agent`)

- **`rig devenv doctor`**: Diagnoses local runtimes, fonts, SDKs, and CLI tools.
- **`rig devenv estate-rules`**: Installs or refreshes the estate-wide rules router under `~/code/personal/AGENTS.md`.
- **`rig agent`**: Invokes configured AI coding assistants (`-agy`, `-ccc`, `-oc`, `-codex`).

### 6. Just-in-Time Discovery

- **`rig help`**: High-level map of all CLI command groups.
- **`rig <group> --help`**: Subcommand flags, options, exit codes, and examples.

______________________________________________________________________

## Global Installation & Continual Use

Under [ADR 0022](../adr/0022-csharp-is-rigs-primary-language.md), `rig` is implemented in C# on .NET 10:

1. **Pack as Global Tool**:
   `src/Rig.Cli/Rig.Cli.csproj` is packaged as a standard .NET tool (`<PackAsTool>true</PackAsTool>`).

   ```bash
   dotnet pack src/Rig.Cli -c Release
   dotnet tool update -g --add-source artifacts/packages Rig.Cli
   ```

1. **System PATH Integration**:
   The compiled tool is installed into `~/.dotnet/tools/rig` and symlinked to `~/.local/bin/rig`. Because `~/.local/bin` is present in system `PATH`, running `rig <command>` works seamlessly across any terminal, harness, or child repository without JIT build overhead.

1. **In-Repo Worktree Execution**:
   Inside an active checkout or worktree of `rig`, developers and agents can execute the current working tree directly:

   ```bash
   dotnet run --project src/Rig.Cli -- <command>
   # or through the Python forwarding shim:
   uv run rig <command>
   ```

______________________________________________________________________

## Context Budget & Progressive Disclosure

Do not dump comprehensive command documentation into individual project `AGENTS.md` files. Individual projects should include only a single pointer line when referencing estate tools:

```markdown
Landing a PR: use `rig gh pr land <PR>` or `rig gh pr wait <PR>` (never loop `gh pr view`). Run `rig help` for estate commands.
```
