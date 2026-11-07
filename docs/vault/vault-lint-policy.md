# Vault lint policy

`uv run vault graph lint` checks the Obsidian vault's frontmatter, filenames and duplicate
notes. `vault graph lint --help` lists every code, its severity, and the `lint_ignore`
frontmatter that silences one. This page holds the reasoning and the repair order.

The codes keep their `TVS` prefix from `the-vault-system`, which is archived; this CLI is what
remains of it.

## Severity

Errors are what makes the vault wrong: unreadable frontmatter, a note with no type or with an
empty required field, and duplicate notes. Warnings are what makes it untidy or fragile: an
unknown note type, and every filename finding. Filenames stay warnings so a portability risk
shows up before it breaks a Windows clone, without blocking a vault that otherwise works.

## Repair order

Every repair is read-only until `--apply`. Run it without first and read what it proposes.

1. `vault graph lint --json` to see the raw findings.
2. Fix invalid frontmatter by hand; nothing else can repair a note it cannot parse.
3. `vault graph repair metadata`, then with `--apply`.
4. `vault graph repair filenames`, then with `--apply`. It renames the file and rewrites the
   wikilinks that point at it.
5. `vault graph repair duplicates`, reviewed before `--apply`.
6. Run `vault graph lint` again.

## Filenames

A filename stem should be ASCII, hyphenated rather than dotted or full of spaces, free of URL
fragments where a human title exists, and stable, because backlinks depend on it.
`Draw.IO.md` becomes `draw-io.md`; `https---twitter.com-JetBrains_Aqua-social.md` becomes
`jetbrains-aqua-social.md`.

It must also be portable: no `*`, `:`, `?`, `<`, `>`, `|`, `"` or `\`, and no Windows device
name such as `CON`, `NUL`, `COM1` or `LPT1`.

Prefer fixing a note to ignoring its finding. Keep `lint_ignore` for a note that is
deliberately outside the policy, and name the specific code rather than `ALL`.
