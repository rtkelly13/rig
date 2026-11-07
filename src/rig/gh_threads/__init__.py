"""TEMPORARY: list, reply to and resolve GitHub pull-request review threads (`rig gh threads`).

`gh` 2.100 cannot touch review threads: there is no `gh pr` subcommand to list them, reply on one or
resolve one, so each of those took a hand-written `gh api graphql` call. The main ruleset requires every
thread resolved before merge, and the rule for agents is to resolve a thread only after the fix is
pushed, replying on it with the fix commit first. This package wraps those GraphQL calls and enforces
that rule (`resolve` refuses a SHA that is not one of the PR's commits).

Retire it once `gh` handles review threads natively: delete this package, its mount in `rig.rig_cli`,
`tests/fakes/gh_graphql.py`, `tests/test_gh_threads.py` and the allowlist entry in
`config/cli-allowlists.json`.
"""
