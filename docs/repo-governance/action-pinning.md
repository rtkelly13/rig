# Action pinning

[← Repo governance](README.md)

Every `uses:` in a workflow is a 40-character commit SHA followed by the exact version it resolves to:

```yaml
- uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0
```

Three upstream tools keep it that way. rig doesn't pin anything itself.

| Job | Tool | Where it runs |
| --- | --- | --- |
| First pin of a workflow | [`pinact`](https://github.com/suzuki-shunsuke/pinact) (`pinact run`), or by hand with `gh api repos/<owner>/<repo>/commits/<tag> -q .sha` | Locally, once, in the PR that adds the workflow |
| Keeping pins current | Dependabot `github-actions` ecosystem (`.github/dependabot.yml`, weekly, 7-day cooldown) | GitHub; it opens PRs that move the SHA and the `# vX.Y.Z` comment together |
| Refusing an unpinned ref | [`zizmor`](https://docs.zizmor.sh/) `--offline`. Its `unpinned-uses` audit is an error, and it also flags `artipacked` and `excessive-permissions` | The repo's CI lint job, with a pinned `zizmor` version |

rig's own setup is the reference: `.github/dependabot.yml` and the `zizmor` step in `ci.yml`'s `lint`
job. Copy both into a repo when it first gets workflows.

`repo-governance status` and `audit` still show a Pinned SHAs column across the estate. It is a
read-only count of refs that aren't SHAs (`inspect_pinned_shas`), so a repo without zizmor in its CI
still shows up. `enforce` reports the count and never rewrites workflows.

## What this replaced

rig used to carry its own pinner: `action_shas.json`, which mapped `owner/action@tag` to `[sha, tag]`;
`action_sha_updater.py`, which refreshed that map from the GitHub API; `align_pinned_shas`, which
rewrote workflows from the map during `enforce`; and a weekly `action-sha-governance.yml` job. It was
retired for three reasons:

- **It jumped major versions without saying so.** The map pointed `actions/checkout@v2`, `@v3` and
  `@v4` all at the v7.0.1 SHA, so `enforce` would silently move a repo three majors.
- **Its update job did nothing.** The weekly workflow couldn't push its own branch, so the map only
  changed when someone ran the updater by hand, and those PRs piled up.
- **rig's own workflows were unpinned.** zizmor found 22 unpinned refs in the repo that owned the
  pinner.

Dependabot resolves each action within its own version line and opens a reviewable PR per bump,
zizmor fails CI on any regression, and none of it is code to maintain here.
