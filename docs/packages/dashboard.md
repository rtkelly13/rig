# Package and version dashboard (`workspace packages dashboard`)

[← Packages](README.md)

`workspace packages dashboard` provides a single, unified, read-only dashboard across every published package declared in `config/managed-projects.json` `packages[]`.

It answers four essential release questions at a glance:

1. **What is published?** Latest versions on public package registries (NuGet and npm).
1. **What is tagged?** Corresponding git release tags across both global (`v<version>`) and package-scoped (`<PackageId>/v<version>`) conventions.
1. **What is unreleased?** Whether unreleased changes are queued under `[Unreleased]` in the project or root `CHANGELOG.md`.
1. **Where is the drift?** Flags missing tags, unreleased tags, unlisted/deprecated artifacts, and version divergence across package families.

## Usage

```bash
# View table dashboard for all declared packages in the estate
rig workspace packages dashboard

# Filter to a specific project (case-insensitive)
rig workspace packages dashboard --project Iceberg.NET

# Machine-readable JSON output for CI and telemetry
rig workspace packages dashboard --json

# Override the code root used to locate local checkouts
rig workspace packages dashboard --root ~/code
```

## Data Sources

The dashboard queries three independent layers and reconciles them:

| Source | Endpoints & Files | What is read |
|---|---|---|
| **NuGet Registry** | `https://api.nuget.org/v3-flatcontainer/<id>/index.json`<br>`https://api.nuget.org/v3/registration5-gz-semver2/<id>/index.json` | Latest version from flat container; listed and deprecation metadata from catalog leaves. |
| **npm Registry** | `https://registry.npmjs.org/<name>` | Latest version from dist-tags / packument versions; deprecation notices. |
| **Git Tags** | Local clone (`git tag -l`) | Matching tag names: package-scoped (`<PackageId>/v<ver>`) or unified repo tags (`v<ver>`). |
| **Changelogs** | `<manifest-dir>/CHANGELOG.md` or `<repo-root>/CHANGELOG.md` | Content presence under the `## [Unreleased]` section header. |

## Flags

The dashboard attaches orthogonal flags to every package where drift or pending work is detected:

| Flag | Meaning | Typical Remediation |
|---|---|---|
| `unreleased-changes` | `CHANGELOG.md` has entries under `[Unreleased]`. | Package has pending features ready for a version bump and release. |
| `tag-without-registry` | A git tag exists with a version not published on the registry. | CI release workflow needs to be triggered, or the publish failed. |
| `registry-without-tag` | A version exists on the registry, but git has no matching tag. | Push the missing release tag to origin (`git tag v<ver> && git push origin v<ver>`). |
| `unlisted` | The latest package release on NuGet is marked unlisted. | Investigate nuget.org package management settings. |
| `deprecated` | Package or latest release is marked deprecated on the registry. | Verify intentional deprecation status and migration path. |
| `version-family-drift` | Packages in the same multi-package project have different latest versions. | Reconcile package versions so the family releases in lockstep. |

When no flags apply:

- **`synced`**: The registry version matches the git tag, the changelog is clean, and the package is active and healthy.
- **`unpublished`**: The package has not yet had an initial registry release and has no release tags.

## JSON Schema

With `--json`, `workspace packages dashboard` outputs a structured array of JSON objects:

```json
[
  {
    "project": "Iceberg.NET",
    "package": "Iceberg.Net.Core",
    "registry": "nuget",
    "manifest": "src/Directory.Build.props",
    "registry_version": "0.0.1-alpha.1",
    "latest_tag": "v0.0.1-alpha.1",
    "tag_version": "0.0.1-alpha.1",
    "changelog_found": true,
    "changelog_unreleased": false,
    "unlisted": false,
    "deprecated": false,
    "flags": [],
    "status": "synced"
  }
]
```
