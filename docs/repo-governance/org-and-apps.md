# Organisation and GitHub Apps

[← Repo governance](README.md)

## Targeting an organisation (`--org`)

Most `repo-governance` commands take `--org <name>` (for example `rtkelly-labs`). It resolves repos in
one of two ways, depending on whether the command works on GitHub or on disk:

1. **Remote discovery.** Commands that act through the GitHub API (`issue-triage`, `pr-triage`,
   `sync-labels`, `environments`) list the organisation's repos with `gh repo list <org>`.
2. **Local filtering.** Commands that act on checkouts (`status`, `audit`, `enforce`, `license`,
   `gitignore`) keep the local repos whose remote, or declared `expected_owner`, is that organisation.

The organisation's profile in `config/repo_policies.json` supplies its `copyright_holder`,
`contact_email` and open-source policies (`license`, `community_templates`, `security_policy`), so
nothing falls back to personal defaults.

## GitHub Apps across accounts

Review bots, connectors and dependency bots (CodeRabbit, the ChatGPT Codex connector, Renovate) are
GitHub Apps, declared under `github_apps` in `config/repo_policies.json`.
`uv run repo-governance apps --help` covers the policy keys, the audit and the repo scoping.

**An app never follows a repo into a new organisation.** GitHub won't carry an app's permissions
across accounts, and an app can't be installed without a person approving it. So after a transfer,
`apps --audit` prints the install URL for each app the new account lacks; once a person has installed
it, `apps --enforce` scopes the repos in.

## Renovate and Dependabot

A repo with a Renovate config (`renovate.json`, `.github/renovate.json5` and the like) must not also
declare Dependabot version updates in `.github/dependabot.yml`: the two open duplicate PRs for the same
upgrade and race each other's CI. `audit` reports a repo with both as `CONFLICT`. Fix it by removing the
version-update entries, or setting their `open-pull-requests-limit` to `0`. Dependabot security alerts
and security fixes are repository settings and stay on.

For the candidate comparison and adoption path, see
[Open Source Code Review Tooling](../open-source/code-review-tooling-adoption.md).
