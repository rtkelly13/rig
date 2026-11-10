# Dependency licences

How a published package proves every dependency it ships has a licence compatible with its own.
**Status: prototype, not yet in `rtkelly-labs/github-actions`.** The work is the patch in
[dependency-licences/](dependency-licences/), waiting to be applied there.

## The approach

- **Lockfiles only.** The input is the committed lockfile (`pnpm-lock.yaml`, `uv.lock`,
  `packages.lock.json`), never a manifest, so the same commit always yields the same package set.
- **One scanner for every ecosystem.** [OSV-Scanner](https://github.com/google/osv-scanner) reads
  those lockfiles and looks up each exact version's licence on deps.dev, which reports SPDX
  identifiers from the npm, NuGet and PyPI registry metadata. It sends package names and versions,
  never code. One binary, pinned by version and SHA-256.
- **Policy as data.** An allow / review / deny list of SPDX identifiers chosen for the licence the
  repo publishes under. Tools enforce allowlists; none decides legal "compatibility", including ORT.
- **Overrides with evidence.** Registry metadata is sometimes missing or wrong, mostly NuGet's
  deprecated `licenseUrl`. A committed override names one exact version, the corrected licence and
  a citation. Unknown fails until someone resolves it.
- **Only what ships.** Dev dependency groups and unpublished projects (tests, benchmarks) are out of
  scope. Attribution obligations apply when code is bundled or vendored, not when a registry
  package references its dependencies.

Not chosen: ORT and ScanCode (thorough, but heavy and slow); per-ecosystem tools such as
`pnpm licenses list`, `pip-licenses` and `nuget-license` (three formats, three sets of quirks).
GitHub Dependency Review stays as a PR-time warning; it depends on GitHub's dependency graph and
is not the deterministic gate.

## What the patch adds to `rtkelly-labs/github-actions`

It follows that repo's two-phase pattern (gather evidence, then judge it offline) and its rule that
actions report advisory codes and never fail the process.

| Path | Purpose |
|:-----|:--------|
| `licence-scan/action.yml` | Downloads the pinned osv-scanner, verifies its SHA-256, scans lockfiles, lists manifests with no lockfile, writes a fingerprinted packet |
| `licence-scan/scanner.json` | The pin: version, SHA-256 per platform, scanner arguments. **Empty in the patch** |
| `licence-check/action.yml` | Judges the packet with no network; outputs Markdown, a JSON inventory and a code (`0` compliant, `1` violation, `2` incomplete) |
| `src/estate_actions/licences.py` | Pure evaluation: SPDX expressions (`OR` least severe, `AND` most severe, `WITH`), policy, overrides |
| `src/estate_actions/licence_scan.py` | The only side effects: download, subprocess, filesystem walk |
| `src/estate_actions/licence_report.py`, `licence_cli.py` | Report and action entry points |
| `tests/test_licences.py` | 23 tests: expression rules (including a Hypothesis property), overrides, dev and path scoping, checksum refusal, an end-to-end scan and check with a stand-in scanner |
| `README.md` | A "Dependency licences" section with the consumer workflow, policy and override formats |

Ruff, mypy (strict) and the full suite (91 tests) passed when it was written.

## How to continue

The session that wrote the patch could not push it: `rtkelly-labs/github-actions` shares its name
with `sentricmusic/github-actions`, and one session can't hold both. Use a session (or a clone)
with `rtkelly-labs/github-actions` only.

1. **Apply the patch** in `rtkelly-labs/github-actions`:

   ```bash
   git checkout -b feat/dependency-licences origin/main
   git am /path/to/rig/docs/open-source/dependency-licences/0001-feat-dependency-licence-scan-and-check-actions.patch
   uv sync --locked && uv run ruff check src tests && uv run mypy && uv run pytest
   ```

2. **Pin the scanner.** Choose an osv-scanner v2 release and copy the hashes for `linux_amd64`,
   `linux_arm64`, `darwin_arm64` and `windows_amd64.exe` from its `osv-scanner_SHA256SUMS` asset
   into `licence-scan/scanner.json`. Until then the scan reports `2` (incomplete) with the reason.
   Add `scanner.json` to Renovate as a custom manager so updates arrive as PRs.

3. **Run the spike the prototype could not.** Run the pinned binary with the arguments in
   `scanner.json` (`--all-packages --licenses=MIT`; the allowlist value only switches licence
   lookups on, the real policy is applied afterwards) on `rig` (pnpm and uv) and
   `Parquet.SourceGenerator` (NuGet with lockfiles). Check three assumptions:
   - every package, not only vulnerable ones, appears with a `licenses` array;
   - dev dependencies carry `dependency_groups` containing `dev` for pnpm and uv;
   - `uv.lock` and `packages.lock.json` get licences, not just `pnpm-lock.yaml`.

   If the shape differs, change `normalise()` in `licences.py` (the only parser) and the canned
   output in `scanner_output()` in the tests. If dev groups are missing for an ecosystem, take
   that ecosystem's production set from its own export (`uv export --no-dev`,
   `pnpm list --prod --json`) in `licence_scan.py`.

4. **Add a smoke job** to `.github/workflows/action-smoke.yml` that runs `./licence-scan` and
   `./licence-check` on the repo itself and asserts code `0`, then open the PR.

5. **Give the .NET repos lockfiles.** Set `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`
   in `Directory.Build.props` and restore with `--locked-mode` in CI in `parquet-dotnet`, `Diffra`,
   `Arrow.SourceGenerator`, `Resultful` and `Lake.NET`. Until then the check reports their projects
   as manifests without a lockfile. For `parquet-dotnet` this is a `lake(NNNN)` change decided in
   Lake.NET first.

6. **Roll out from rig.** Add the consumer workflow from the github-actions README through
   repo-governance, then add `licences` to `required_checks` in `config/repo_policies.json` and run
   `enforce`, in the order [required-checks.md](../repo-governance/required-checks.md) sets. Repos
   with test or benchmark projects add `.github/licence-policy.json` with `exclude_paths`.

Possible follow-up in rig itself: a `rig licences` command that runs the same scan and check
locally across the estate, reusing the `estate_actions` modules rather than a second
implementation.
