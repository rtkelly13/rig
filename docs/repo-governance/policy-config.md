# Policy config and scope

[← Repo governance](README.md)

## Policy lives in config, not in Python

`config/repo_policies.json` is the source of truth for what governance
expects. It has two layers:

- `defaults` — applies to every repo.
- `profiles.<name>` — may override any `defaults` key, and lists the repos it
  covers.

Overrides **replace** rather than merge. A profile that declares
`gitignore_entries` supplies the whole list. Deep merge reads as convenient
until you need to *remove* an inherited entry and find you cannot.

A repo in no profile still gets `defaults`, so governance applies to a new repo
before anyone remembers to register it.

### Where an unregistered repo lives: `owner_profiles`

`owner_profiles` maps a GitHub owner to a profile (`"rtkelly-labs": "rtkelly_labs"`;
case-insensitive, and every value must name a declared profile or loading fails). A repo
no profile names, but whose owner is mapped, takes only that profile's **identity** keys:
`expected_owner`, `allowed_owners`, `copyright_holder`, `contact_email` and `license`.
Its rules (required checks, settings, environments, policies) stay off until a profile
lists it, so `enforce` never pushes a ruleset nobody opted into. `status` and `audit`
show it as an advisory, `not adopted: add it to profile <name>`, not as drift.

Lookups are keyed by `policy_key(path)`: `owner/name` from `remote.origin.url` (the
remote's name, not the folder's), else `<org>/<folder>` for a clone in an org folder
beside `personal/`, else the bare folder name. Profiles and `repo_overrides` still match
on the last segment, so bare names keep working.

Read it through `policy_config.load_policy_config()`; never re-parse the file.

### `gitignore_entries`

```json
{
  "name": "temp",
  "comment": "Temporary files, databases, scratch files, and worktrees",
  "write": ["/temp/", "temp/"],
  "satisfied_by": ["/temp", "temp/"]
}
```

`write` is appended when the entry is missing; `satisfied_by` is what counts as
already present. They differ because one intent has several spellings — `/temp`
and `temp/` both ignore the scratch directory, and appending a second spelling
to a file that already has one is churn, not enforcement. Only missing entries
are appended, so `enforce` is idempotent.

### `repo_settings`

The merge settings `plan` (and `enforce`) hold a repo to, sent as one `PATCH repos/<repo>`
together with the default branch from `expected_base_branch`:

```json
{
  "delete_branch_on_merge": true,
  "allow_squash_merge": true,
  "allow_merge_commit": false,
  "allow_rebase_merge": false,
  "allow_auto_merge": true
}
```

A key left out takes the value shown. `allow_auto_merge` is not drift on a private repo:
GitHub ignores it there on Free plans.

### `labels`

The label taxonomy, a list of `{"name", "color", "description"}` (`color` is six hex digits,
no `#`). A missing label is created and one whose colour or description differs is updated;
labels outside the list are left alone. An empty list (or none) leaves a repo's labels
ungoverned.

## Which repos governance touches

`defaults.repo_selection` decides whether a repo is in scope at all during an
`--all` sweep:

| Key | Default | Effect |
| --- | --- | --- |
| `manage_forks` | `false` | Forks are skipped. |
| `require_push_access` | `true` | Repos you cannot push to are skipped. |
| `exempt` | `false` | Set by a profile to opt its repos out entirely. |

**A repo named in any profile's `repos` list is always managed**, whatever
those say. Listing a repo is a deliberate act; second-guessing it would make
the config a suggestion. The one exception is a profile that sets
`repo_selection.exempt`: `demos_experiments` lists demos, bug reproductions and
experiments so that leaving them ungoverned is recorded, not accidental.

Forks we *do* manage, which track an upstream and publish under their own name,
have their own profile and ruleset: see [../forks.md](../forks.md).

This exists because a clone of someone else's repo is indistinguishable from
your own on disk. A clone of `tailwind-nextjs-starter-blog` (now in
`~/code/external/reference/`) has `timlrx/...` as its origin, and before this
filter a cleanup dry run happily proposed deleting 30+ branches on that third
party's repository.

An explicit single path is **not** filtered — naming a repo on the command line
is itself explicit. Mutating commands carry their own guards regardless.

## Shared .gitignore entries

Every managed repo ignores `temp/` and both `TODO.md` and `todo.md`: on a case-sensitive filesystem
`TODO.md` doesn't ignore `todo.md`, and an unignored task file gets committed by mistake.
`rig repo-governance gitignore --help` covers the audit and sync.
The audit also asks git whether `temp/.rig/`, rig's
[per-worktree state folder](worktree-state.md), is really ignored, and flags a rule that re-includes it.

## Filesystem, Filename & Script Language Governance (`ls-lint`)

Estate convention rule 9 establishes a strict script language policy: automation scripts in `scripts/` must use the repository's approved language stack (e.g., C# `.cs` for .NET, TypeScript/JavaScript for Node, Python `.py` for Python), while alien scripting runtimes and legacy shell scripts (`.sh`, `.ps1`) are strictly barred.

This policy is governed centrally through `repo_policies.json` and enforced via `.ls-lint.yml`:
- `repo_policies.json` maps `language_presets` to `script_extensions` and `banned_script_extensions`.
- `RepoHygiene.cs` dynamically renders scoped `.ls-lint.yml` rules with closed allowlists (`.*: exists:0`).
- Local hygiene (`pnpm dlx @ls-lint/ls-lint`) and CI workflows (`ls-lint/action`) gate any PR introducing alien scripts.

For the comprehensive guide covering the full spectrum of filesystem linting capabilities (anti-root-pollution, ADR naming, test suite structures, brand asset isolation, and directory casing), see:
👉 [**Filesystem, Filename & Script Language Governance (`ls-lint`)**](filesystem-and-filename-governance.md)
