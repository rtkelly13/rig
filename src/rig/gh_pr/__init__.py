"""Wait for a pull request to land, and say why when it cannot (`rig gh pr wait|land`).

`gh pr merge --auto` queues a merge and returns; nothing in `gh` then says whether the PR is still
moving. A shell loop that watches only for MERGED or a failed check waits for ever on a PR that is
blocked by something else, such as unresolved review threads under a ruleset that requires them resolved.
This package polls one PR, decides from each observation whether it can still land without anyone
acting, and stops with the reason as soon as it cannot.

`github` observes, `model.assess` decides (pure), `wait` runs the poll loop against an injected clock,
and `cli` renders. The unresolved threads come from `rig.gh_threads.github.fetch_threads`; when that
package is retired, read them from whatever `gh` offers instead.
"""
