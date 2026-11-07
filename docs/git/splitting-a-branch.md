# Splitting a branch into a stack

A branch that grew past reviewability — thirty commits, seven thousand lines —
becomes a chain of PRs, each answering one question.

## Look for contiguous runs first

The expensive version of this rewrites history to group related commits. It is
usually unnecessary: work done in sittings is already grouped in time, so the
commits often partition into **contiguous chronological runs**. If they do, the
split needs no rewriting at all — only branches pointed at boundary commits.

```bash
git log --oneline --reverse "$BASE..$TIP" | nl -ba
```

Read the list and find the seams. Then:

```bash
git branch layer/01-name <sha-at-boundary-1>
git branch layer/02-name <sha-at-boundary-2>
git push origin layer/01-name layer/02-name …
```

Each PR bases on the previous one. Nothing is rebased, nothing is rewritten, and
every commit keeps its message and hash.

Measure the layers before committing to the seams — a layer of 7,000 lines and
one of 300 is a sign the boundary is in the wrong place:

```bash
git diff --shortstat layer/01-name layer/02-name
```

## Intermediate commits have never been tested

This is the part that surprises. CI only ever ran on the branch **tip**, so every
boundary commit is unverified — and the tip being green says nothing about them.

Expect the split to expose:

- **Formatting a later commit fixed.** A generated fixture that never matched the
  formatter, corrected by hand three commits later.
- **A lint rule a later commit satisfied.** Intermediate states legitimately fail
  a rule the final state passes.
- **Scratch files removed at the end.** Test files committed by accident and
  deleted in the last commit still sit in the middle layers — and there they run.

So verify each boundary **at that boundary**, not by inference from the tip:

```bash
for b in layer/01-name layer/02-name …; do
  git worktree add --detach /tmp/wt "$b"
  (cd /tmp/wt && pnpm lint && pnpm typecheck && pnpm test)
  git worktree remove --force /tmp/wt
done
```

## Repairing a layer

Fixing an intermediate layer means rewriting it and restacking everything above:

```bash
git checkout -B layer/01-name <boundary>
# …fix…
git commit -am "…"
git rebase --onto layer/01-name <old-boundary-01> layer/02-name
git rebase --onto layer/02-name <old-boundary-02> layer/03-name
```

To strip a file from every commit in a layer — scratch files, a leaked secret —
an `--exec` sweep is cleaner than an interactive rebase:

```bash
GIT_SEQUENCE_EDITOR=true git rebase -i "$BASE" --exec \
  'if git ls-files --error-unmatch tests/scratch.test.ts >/dev/null 2>&1; then
     git rm -q -f tests/scratch.test.ts && git commit -q --amend --no-edit
   fi'
```

## The invariant that makes it safe

After repairing and restacking, the **tip must be unchanged** except for the
fixes you intended:

```bash
git diff safety/pre-repair "$NEW_TIP"
```

If that shows anything you did not deliberately change, the restack lost work.
This one check is worth more than reading every intermediate diff.

## Ordering the layers

Sequence so each PR is a claim the next one builds on. What worked:

1. the mechanism / module system
2. the things built on it
3. contracts and CI that constrain them
4. the consumer-facing surface and docs

Reviewers then meet the abstraction before its twenty users, rather than
inferring it from them.

## If the PR is already registered in a GitHub stack

It cannot be retargeted — see
[stacked-pull-requests.md](stacked-pull-requests.md). Close it and open a
replacement from the same head branch onto the new base.
