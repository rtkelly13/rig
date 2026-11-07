# Merging GitHub stacked pull requests

A pull request registered in one of GitHub's **managed stacks** cannot be merged
by `gh pr merge`, and cannot have its base branch changed at all. Both refuse
with errors that name the stack but not the way out. This is the way out.

## The symptom

```
$ gh pr merge 121 --squash
GraphQL: This pull request is part of a stack and must be merged using the
asynchronous merge REST API. (mergePullRequest)

$ gh api -X PUT repos/OWNER/REPO/pulls/121/merge -f merge_method=squash
{"message":"Merging stacked PRs via this endpoint is not supported.
 Use the asynchronous merge endpoint instead.","status":"403"}
```

The error points at `docs.github.com/rest/pulls/pulls#merge-a-pull-request-asynchronously`,
which documents the endpoint but is easy to miss. The path uses a **hyphen**:

```
PUT /repos/{owner}/{repo}/pulls/{pull_number}/merge-async
```

`merge_async` with an underscore is a 404, which is a plausible guess and wastes
several minutes.

## Using it

```bash
gh api -X PUT repos/OWNER/REPO/pulls/121/merge-async -f merge_method=squash
```

```json
{"status":"pending",
 "details":{"message":"Merge request enqueued.",
            "uuid":"31e83c74-1333-4724-af43-08b11a049aa1",
            "merge_method":"squash",
            "merge_action":"default",
            "expected_head_sha":"d2d9aaa…"}}
```

Body parameters are the same as the synchronous endpoint — `merge_method`
(`merge` | `squash` | `rebase`), `commit_title`, `commit_message`, `sha` — plus
`merge_action` (`default` | `direct_merge` | `merge_queue`).

Calling it again on an already-merged PR is safe and returns the result rather
than an error:

```json
{"status":"merged","details":{"message":"Pull request is already merged.","sha":"e20daf6…"}}
```

## The part that will bite you

**`pending` is not success.** The endpoint enqueues, so most of the checks that
the synchronous endpoint performs up front happen *afterwards*. A pull request
with merge conflicts is accepted happily:

```bash
$ gh api -X PUT repos/OWNER/REPO/pulls/107/merge-async -f merge_method=squash
{"status":"pending","details":{"message":"Merge request enqueued.","uuid":"25c00a20…"}}
```

…and then never merges. Nothing tells you. A script that treats HTTP 202 or
`status: pending` as "merged" will report success for a merge that silently did
not happen — and, worse, will do so for every conflicting PR it is pointed at.

Not everything is deferred: a draft PR fails synchronously with HTTP 400 and
`{"status":"failed","details":{"message":"Pull request is in draft."}}`. So the
absence of an immediate error tells you nothing either way.

### Poll the uuid

The `uuid` from the enqueue response is queryable, and it carries the real
outcome:

```bash
GET /repos/{owner}/{repo}/pulls/{pull_number}/merge-async/{uuid}
```

```json
{"status":"failed","details":{"message":"Pull Request has merge conflicts"}}
```

`status` is one of `pending`, `merged`, `enqueued`, `failed`. Poll until it
leaves `pending`.

## Merging one PR without trusting `pending`

Enqueue with `PUT repos/{owner}/{repo}/pulls/{n}/merge-async`. A `pending` response means enqueued, not merged: the outcome exists only on the returned `details.uuid`. Poll `GET …/merge-async/{uuid}` until it reports `merged` (with the merge `sha`) or `failed` (with a message), and treat anything still pending after a minute or two as needing a look at the PR.

## Stacks retarget themselves, and then conflict

One good surprise and one bad one, both worth expecting.

**Good:** merging the bottom of a stack **automatically retargets its child to
`main`**. No `gh pr edit --base` needed — which is fortunate, because a stacked
PR refuses that too:

```
GraphQL: Cannot change the base branch because the pull request is part of a stack.
```

**Bad:** if the parent was **squash-merged**, the retargeted child immediately
goes `DIRTY`. Its branch still contains the parent's original commits, which
now conflict with the single squashed commit on `main`. The fix is a rebase that
drops them:

```bash
git rebase --onto origin/main <old-parent-branch-tip> <child-branch>
```

After that the child contains only its own commits and merges cleanly. Expect
one of these per level of the stack.

## When you cannot merge it at all

If the endpoint is unavailable or the PR is stuck in some other way, the escape
hatch is to **close the PR and open a new one from the same head branch**.
Stack registration does not survive, so the replacement behaves normally. The
cost is the PR number and its comment history, so it is a last resort — but it
is reliable, and it is the only route when a stacked PR needs a base change that
the API will not perform.

**Retarget children before deleting a parent branch.** Deleting a base branch
auto-closes every PR based on it, and a closed PR can be neither reopened nor
retargeted — recreating the branch afterwards does not undo it. Merge parents
with the branch left in place, retarget the children, and delete afterwards or
not at all.
