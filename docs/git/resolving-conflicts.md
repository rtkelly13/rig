# Resolving conflicts

Almost every conflict encountered while restacking these repositories has the
same shape, and the same resolution. The danger is not deciding *what* to keep —
it is the mechanics of keeping it.

## Nearly all of them are "both sides appended"

Two branches each added an entry to the end of a list. Examples that have
actually occurred:

| File | `main` added | Branch added | Answer |
|:--|:--|:--|:--|
| `tsconfig.json` `exclude` | `"temp"` | `"video"` | both |
| `.gitignore` | `.vercel` | a reference-material rule | both |
| `package.json` deps | `@tanstack/react-table` | three `@radix-ui/*` | both |
| `src/index.ts` | two `export *` | three `export *` | both |
| `scripts/check-deps.mjs` | one entry | three entries | both |

The resolution is "keep both sides", every time. Which makes it tempting to
automate — and that is where it goes wrong.

## Do not resolve by regex

Stripping the markers with a pattern like

```python
re.sub(r'<<<<<<< HEAD\n(.*?)=======\n(.*?)>>>>>>> [^\n]*\n', r'\1\2', s, flags=re.S)
```

works on flat lists and silently corrupts structured files. Git places the
conflict boundary wherever the common context ends, which for an object literal
is **inside the braces**:

```js
  '@tanstack/react-table': {
    kind: 'runtime',
    why: '…',
<<<<<<< HEAD
=======
  },
  '@radix-ui/react-dropdown-menu': {
```

Concatenating the two halves drops the `},` and produces a file that no longer
parses. This shipped once, because `eslint src` did not cover `scripts/`, and
was only caught when the same trap reappeared in the next PR.

## Parse every resolved file before continuing

Cheap, and it would have caught the above immediately:

```bash
node --check path/to/file.mjs                       # JS / MJS
node -e "JSON.parse(require('fs').readFileSync('package.json','utf8'))"
node -e "require('yaml').parse(require('fs').readFileSync('f.yml','utf8'))"   # YAML
```

For YAML, use a **real parser**. `js-yaml` ships no CLI binary, so
`npx js-yaml file.yml` does not validate anything — resolve the module from the
package store and call it:

```bash
P=$(find node_modules/.pnpm -maxdepth 1 -name 'yaml@*' | head -1)
node -e "require('$PWD/$P/node_modules/yaml').parse(require('fs').readFileSync('f.yml','utf8'))"
```

Note that YAML parses `on:` as the boolean `true`, so `d.on` is undefined in a
workflow file. That is not a syntax error.

## Lockfiles: regenerate, never merge

A conflicted `pnpm-lock.yaml` is not worth reading. Take either side, then
rebuild it from the resolved manifest:

```bash
git checkout origin/main -- pnpm-lock.yaml
pnpm install --lockfile-only
```

Then confirm both dependency sets survived, rather than assuming:

```bash
grep -c 'tanstack/react-table' pnpm-lock.yaml
grep -c 'radix-ui/react-popover' pnpm-lock.yaml
```

## Conflicts that are really a decision

Occasionally both sides changed the same thing for different reasons, and
"keep both" is wrong:

- **A renamed CI job.** One side renamed it to a short durable name, the other
  extended the descriptive one. The rename wins — a name that describes contents
  cannot also be a stable contract.
- **A reworked component.** `main` replaced an interface with a discriminated
  union while the branch added documentation to the old shape. Keep `main`'s
  shape and port the prose onto it; do not resurrect the old type.

When a conflict encodes a disagreement rather than an overlap, resolve it in
favour of `main` and carry the branch's *intent* across, not its text.

## After a squash merge

If a parent was squash-merged, a child branch still contains the parent's
original commits and will conflict against the single squashed commit. This is
not a real conflict — drop the duplicates:

```bash
git rebase --onto origin/main <old-parent-branch-tip> <child-branch>
```

See [stacked-pull-requests.md](stacked-pull-requests.md).
