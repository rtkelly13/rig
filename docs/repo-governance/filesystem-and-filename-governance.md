# Filesystem, Filename & Script Language Governance (`ls-lint`)

> [!TIP]
> This guide is part of the estate governance architecture. For the foundational repository conventions, CI gate patterns, and the full project checklist, see the central hub: [**Open Source Project Expectations**](../open-source/open-source-project-expectations.md).

This document defines the architecture, policies, and expanded capabilities of filesystem, directory, and filename governance across the estate. It details how [`ls-lint`](https://ls-lint.org/) is integrated into `repo-governance` to enforce naming conventions, closed file-extension allowlists, and repository language purity.

---

## 1. Why Filesystem Governance Matters

In modern codebases—especially those co-authored with autonomous AI coding agents—the filesystem is frequently the first boundary to erode:

1. **Ecosystem & Language Drift**: AI agents habitually drop ad-hoc scripts written in their favorite language (often Python or Bash) into foreign projects (such as C# .NET solutions or TypeScript web applications) without regard for the project's runtime dependencies or developer tooling.
2. **Platform Incompatibilities**: Shell scripts (`.sh`) fail or require emulation on Windows workstations; PowerShell scripts (`.ps1`) require extra runtimes and execution policy overrides on Linux/macOS. Neither is acceptable when cross-platform, typed runtimes (`uv run`, `pnpm`, `dotnet run`) are the established standard.
3. **Rogue File Sprawl**: Uncurated scratch files (`temp.py`, `debug.json`, `test-output.txt`, `sample.csv`) and one-off helper scripts pollute repository roots and feature directories.
4. **Casing Inconsistencies & OS Gotchas**: Inconsistent capitalization (`NavBar.tsx` vs `navbar.tsx`, `MyComponent.test.ts` vs `my_component_test.ts`) causes silent git checkout failures, broken imports on case-sensitive Linux CI runners, and dirty worktrees.
5. **Architectural Bypass**: Code or assets placed in arbitrary subdirectories bypass build harnesses, vector simplifies, or doc generators that expect disciplined hierarchies.

---

## 2. Tooling Architecture: Why `ls-lint`?

We adopt [`ls-lint`](https://ls-lint.org/) (`@ls-lint/ls-lint` on npm, `ls-lint/action` on GitHub Actions) as our estate-wide filesystem validator because it provides properties AST linters and shell scripts lack:

- **Sub-Millisecond Execution**: Written in Go as a native static binary, `ls-lint` inspects tens of thousands of directories and files in under 15 milliseconds. It runs with zero runtime startup penalty in pre-commit hooks, local health scripts (`uv run status`), and CI jobs.
- **Cross-Platform Uniformity**: Binary distributions run identically on macOS, Linux, and Windows, eliminating differences in POSIX shell globbing, find commands, or Windows PowerShell quirks.
- **Closed Allowlists (`.*: exists:0`)**: Rather than playing whack-a-mole with blacklist patterns, `ls-lint` supports a closed-world allowlist model. Explicitly declaring acceptable extensions and appending `.*: exists:0` immediately rejects any unexpected file extension.
- **Deterministic Structural Rules**: Validates directory naming (`.dir`), file extensions, multi-suffix patterns (`.test.ts`, `.spec.tsx`), and regular expressions.

---

## 3. Official Rule Engine & Configuration Syntax

Based on the official `ls-lint` specification, the engine exposes a rich set of built-in casing rules, presence assertions, and regex operators:

### Built-in Casing Rules
| Rule | Pattern Example | Description |
| :--- | :--- | :--- |
| `kebab-case` | `user-profile.ts` | Lowercase words separated by hyphens |
| `camelCase` | `userProfile.ts` | Lowercase first word, capitalized subsequent words |
| `PascalCase` | `UserProfile.tsx` | All words capitalized with no separator |
| `snake_case` | `user_profile.py` | Lowercase words separated by underscores |
| `pointcase` (`point.case`) | `user.profile.spec.ts` | Lowercase words separated by periods |
| `lowercase` | `readme.txt` | Lowercase letters with no restriction on symbols |
| `UPPERCASE` | `LICENSE` | Capital letters with no restriction on symbols |

### Custom Regex & Advanced Operators
- **Implicit Anchors**: Any pattern provided as `regex:<pattern>` is automatically anchored from start to end as `^{pattern}$`.
- **Alternation (`|`)**: Combine multiple rules or regexes (e.g. `kebab-case | camelCase` or `regex:^[0-9]+$ | kebab-case`).
- **Regex Negation (`regex:!<pattern>`)**: Enforces that a filename must **not** match a given regex.
- **Dynamic Directory Substitutions (`${0}`, `${1}`)**: References parent directory names dynamically. For example, in component libraries:
  ```yaml
  ls:
    components/*:
      .tsx: regex:${0}   # Enforces components/Button/Button.tsx
      .test.tsx: regex:${0} # Enforces components/Button/Button.test.tsx
  ```
- **File Presence Assertions**:
  - `exists:0`: Strictly prohibits matching files (used for `.*: exists:0` allowlists or targeted bans like `.sh: exists:0`).
  - `exists:1`: Enforces that a file with the given extension/pattern must exist.
- **Directory Validation (`.dir`)**: Placed within any directory block to validate the casing of child directories themselves.

---

## 4. Spectrum of Capabilities & Estate Applications

Beyond banning `.sh` and `.ps1` scripts, `ls-lint` serves multiple high-value governance utilities across our repositories:

### A. Script Language Containment (`scripts/`)
Automated scripts must adhere to the host repository's primary language stack:
- **C# / .NET Repos** (`blobflags`, `ProceduralGeneration3DMazes`): Only `.cs` (executed via `dotnet run <script>.cs`) is permitted. Out-of-ecosystem `.py`, `.ts`, `.sh`, `.ps1` scripts are strictly rejected.
- **TypeScript / Node Repos** (`blog`, `docs`, `ynab-budget-companion`): Only `.ts`, `.js`, `.mjs`, `.cjs` (executed via `pnpm`, `node`, or `tsx`) are permitted.
- **Python Repos** (`rig`): Only `.py` and `.ipynb` (executed via `uv run <script>.py`) are permitted.
- **Universal Data & Documentation**: Standard non-executable support formats (`.md`, `.json`, `.yml`, `.yaml`, `.sql`, `.toml`, `.csv`) are allowed alongside the host script language.
- **Enforcement Pattern**:
  ```yaml
  ls:
    scripts:
      .cs: kebab-case | snake_case | PascalCase
      .md: kebab-case
      .json: kebab-case
      .*: exists:0
    "scripts/**":
      .cs: kebab-case | snake_case | PascalCase
      .md: kebab-case
      .json: kebab-case
      .*: exists:0
  ```

### B. Repo Root Hygiene & Anti-Pollution (`.`)
AI agents and local runs frequently dump stray logs, test outputs, or scratch code in the root directory. `ls-lint` locks down the root directory to only explicitly approved configuration manifests:
- **Allowed in Root**: `README.md`, `AGENTS.md`, `TODO.md`, `LICENSE`, `package.json`, `pyproject.toml`, `Cargo.toml`, `.gitignore`, `.ls-lint.yml`, etc.
- **Rogue Scripts Blocked**: Any root-level `temp.py`, `script.js`, `scratch.sh`, or loose data file immediately triggers a lint failure.

### C. Directory & Subdirectory Casing (`.dir`)
Mixed casing in directory structures breeds confusion and breaking import paths across platforms:
- **Web & Monorepos**: Enforce `.dir: kebab-case` across `src/**`, `apps/**`, and `packages/**`.
- **Python Packages**: Enforce `.dir: snake_case` for Python package submodules (`src/rig/**`).
- **C# Namespaces**: Enforce `.dir: PascalCase` or `kebab-case` matching namespace conventions.

### D. Component & Module Co-location Structure
In React and frontend codebases:
- Enforce that components are strictly `PascalCase.tsx`.
- Enforce that component tests match `PascalCase.test.tsx` or `kebab-case.test.ts`.
- Enforce that Storybook stories match `PascalCase.stories.tsx`.
- Reject loose style files or uncurated helper files outside designated paths.

### E. Test Suite Structure & Naming Conventions
- **Python**: Enforce that files under `tests/**` follow `test_*.py` or `conftest.py` naming:
  ```yaml
  ls:
    tests:
      .py: regex:^test_[a-z0-9_]+$ | regex:^conftest$
    "tests/**":
      .py: regex:^test_[a-z0-9_]+$ | regex:^conftest$
  ```
- **TypeScript**: Enforce `*.test.ts`, `*.test.tsx`, or `*.spec.ts`.

### F. Architecture Decision Records & Documentation Layout (`docs/`)
- Enforce that all documentation files are Markdown (`.md` or `.mdx`), blocking loose documents, binary files, or stray images.
- Enforce ADR naming patterns in `docs/adr/`:
  ```yaml
  ls:
    docs/adr:
      .md: regex:^[0-9]{4}-[a-z0-9-]+$
  ```
  Every ADR must conform to the 4-digit zero-padded index followed by a kebab-case slug (e.g. `0018-brand-asset-governance.md`).

### G. Brand Asset Governance & Production Cuts (`assets/`)
In alignment with [ADR 0018](../adr/0018-brand-asset-governance.md):
- Enforce that vector masters are named `master.svg` or `<project>.svg`.
- Enforce that production cuts manifests are strictly `production-cuts.csv`.
- Disallow loose, untracked raster renders in project asset roots:
  ```yaml
  ls:
    "assets/*":
      .svg: kebab-case
      .csv: regex:^production-cuts$
      .*: exists:0
  ```

### H. Credential, Secret & Dotfile Leak Prevention
Prevent sensitive files and system detritus from being checked in, even if `.gitignore` fails:
- Reject `.env*`, `.pem`, `.key`, `.p12`.
- Reject OS artifacts like `.DS_Store`, `Thumbs.db`, `desktop.ini`.

---

## 5. Central Policy Integration in `repo-governance`

Rather than manually authoring `.ls-lint.yml` across dozens of estate repositories, configuration is generated and kept synchronized centrally by `repo-governance`.

### 1. Specification in `config/repo_policies.json`
Host languages declare their allowed and banned script extensions under `language_presets`:
```json
{
  "language_presets": {
    "dotnet": {
      "script_extensions": [".cs"],
      "banned_script_extensions": [".py", ".sh", ".ps1", ".bat", ".cmd", ".ts", ".js", ".mjs"]
    },
    "node": {
      "script_extensions": [".ts", ".js", ".mjs", ".cjs"],
      "banned_script_extensions": [".py", ".cs", ".sh", ".ps1", ".bat", ".cmd"]
    },
    "python": {
      "script_extensions": [".py", ".ipynb"],
      "banned_script_extensions": [".sh", ".ps1", ".bat", ".cmd", ".cs", ".ts", ".js"]
    }
  }
}
```

### 2. Rendering & Inspection via `RepoHygiene.cs`
- `RepoHygiene` (in `src/Rig.Core/Governance/RepoHygiene.cs`) verifies that `.ls-lint.yml` exists, bans `.sh` and `.ps1`, and enforces `scripts/` rules matching the repo's language preset.
- Its align path generates or updates the compliant `.ls-lint.yml`. It does not check or add a CI step: wiring ls-lint into a repo's workflow is done by hand.

### 3. Agent Convention Rule 9 in `AGENTS.md`
All repository agent prompts (`AGENTS.md`) encode the invariant:
```markdown
9. Script Language Policy: Automation scripts in scripts/ MUST use the repository's
   approved language stack (e.g., C# .cs for .NET, TypeScript/JavaScript for Node,
   Python .py for Python). Alien scripting runtimes (e.g., .py in C# repos, .sh/.ps1
   everywhere) are strictly prohibited and gated by CI.
```

---

## 6. CLI Runtime Options & Monorepo Inheritance

`ls-lint` provides powerful runtime flags for debugging, reporting, and monorepo configuration layering:

### Command Line Flags
| Flag | Description | Typical Use Case |
| :--- | :--- | :--- |
| `--config <path>` | Specify custom configuration file path. Can be repeated. | Merging global estate configs with per-package overrides |
| `--debug` | Output detailed trace of all matched files, directories, and rules. | Investigating regex edge cases and ignore boundaries |
| `--warn` | Log lint violations as warnings and exit with code `0`. | Non-blocking telemetry or staged repo migration rollouts |
| `--error-output-format [text\|json]` | Format error outputs as raw text or structured JSON. | CI annotations, status webhooks, or custom lint parsers |
| `--workdir <dir>` | Changes the root directory before running the check. | Monorepo package execution (`-workdir packages/foo`) |

### Multi-Config Merging (Monorepos & Workspace Packages)
`ls-lint` allows repeating `--config` to inherit and override configurations:
```bash
ls-lint --config .ls-lint.base.yml --config packages/web/.ls-lint.yml
```
When multiple configs are supplied:
1. `ls:` rule maps are shallow-merged (package-level rules override base keys).
2. `ignore:` lists are concatenated, deduplicated, and sorted.

---

## 7. Local Execution & CI Gating

### Local Usage
Run `ls-lint` directly from the repository root:
```bash
# Fast node-based execution (standard in all JS/TS & full estate projects)
pnpm dlx @ls-lint/ls-lint

# Direct binary execution (when installed globally or in Go environments)
ls-lint
```

### GitHub Actions CI Gate
Every managed repository includes the lightweight `ls-lint` step in its primary CI workflow:
```yaml
- name: Verify filesystem structure & script extensions
  uses: ls-lint/action@a87c5eb4fb26d7af1bd0adba203dd1e76f7e94eb # v2.3.2
  with:
    config: .ls-lint.yml
```

---

## 8. Operational Gotchas & Best Practices

1. **Working Directory & Relative Ignore Paths**:
   When invoking `ls-lint` via CLI or actions, always execute with `cwd` set to the repository root. Setting `-workdir <path>` can prevent relative ignore patterns (like `scripts/download-snapshots.sh`) from matching correctly.
2. **Multi-Dot Candidate Extensions**:
   `ls-lint` evaluates candidate extensions from longest to shortest. A file named `setup.local.py` tests candidate `.local.py` before `.py`. When designing closed allowlists, ensure that either multi-suffix files follow explicit patterns or that auxiliary tools validate extension basenames.
3. **Empty / Missing Directories**:
   `ls-lint` safely skips directory rules if the matching folder does not exist on disk. Repositories without a `scripts/` directory evaluate cleanly, but the moment a `scripts/` folder is created, all closed-allowlist rules immediately take effect.
4. **Symlinks and Junctions**:
   On Windows runners, symlinks or directory junctions can cause unexpected recursion if not ignored. Always ensure build output directories (`temp/`, `dist/`, `build/`, `node_modules/`, `.venv/`) are listed in `ignore:`.
