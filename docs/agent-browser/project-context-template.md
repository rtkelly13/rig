# Managed Project review context

Active scheduled reviews use the [live-policy bootstrap](../project-reviews/bootstrap.md),
rendered from [native bindings](../../config/chatgpt-projects.json). Save that same
small bootstrap in each active Project's managed review instructions and task.
Repository scope, focus, coverage/finding rules and report format are fetched
from the matching policy and shared contract on main at every run.

Replace only the old `rig-managed-project:v1` block or existing
`rig-review-bootstrap:v2` block, preserving human instructions outside markers.
Reject ambiguous/duplicate markers rather than replacing unrelated context.
Do not keep a second inline copy of the review policy alongside the bootstrap.
Resultful and c11n are retained contexts with no independent active review task.
Their existing instructions/chats remain as documented in the binding manifest.

The original full inline template remains available in Git history. It is not
the active authority for new reviews. See [policy editing and rollout](../project-reviews/README.md).
