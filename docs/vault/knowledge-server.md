# The shared OpenKnowledge server

OpenKnowledge serves the vault's skill library (`skills({ cwd: <vault> })`) and its editing tools.
Registered the way `ok init` does it, as a stdio server, every Claude session starts its own
`npx … ok mcp`: about 120 MB each (npm exec plus node), measured on this Mac. One resident server
serves the same tools to every session instead.

`vault knowledge-server` runs that server as a macOS LaunchAgent (`dev.rtkelly.rig.open-knowledge`):

```
uv run vault knowledge-server install          # pinned package + LaunchAgent; idempotent
uv run vault knowledge-server connect          # dry run: shows the claude mcp commands
uv run vault knowledge-server connect --apply  # point Claude Code's user-scope entry at it
uv run vault knowledge-server status           # package, agent, health, Claude entry; exit 1 if any fail
uv run vault knowledge-server restart
uv run vault knowledge-server uninstall
```

| | |
|:--|:--|
| Connector | `http://127.0.0.1:47311/mcp` (streamable HTTP) |
| Editor | `http://127.0.0.1:47311/` |
| Package | `@inkeep/open-knowledge` pinned in `KnowledgeServer.Version` (`src/Rig.Core/Vault/KnowledgeServer.cs`), installed under rig's data folder |
| Log | `<rig data>/logs/open-knowledge.log` |

## What it serves

The server is rooted at the vault repository (`~/code/personal/obsidian-vault`), so every call must
name the vault: a `cwd` elsewhere is refused with "escapes the configured root". That is how rig
already uses it ([ADR 0019](../adr/0019-skills-split-between-harness-baseline-and-vault-library.md)):
the domain skills live in the vault and every lookup passes `cwd: <vault>`. The vault is the only
OpenKnowledge project on this machine; a second one would need its own server.

## Choices, and why

- **Full mode, not `--only server`.** Server-only answers `/` with 404, so there is no editor; full
  mode adds the browser editor for reviewing Markdown. Memory did not separate by mode: it varies
  with indexing, roughly 60–330 MB observed in either.
- **`--idle-shutdown off`.** The default shuts down after 30 minutes without clients, and with
  `KeepAlive` launchd would restart it every half hour.
- **Node runs the CLI module directly.** launchd has no shell `PATH`, so the plist names the
  absolute `node` and `dist/cli.mjs` rather than the `ok` shebang script.
- **Pinned, not `@latest`.** The resident server changes only through a PR that bumps `VERSION`.
- **`claude mcp`, not a file edit.** Every running session rewrites `~/.claude.json`, so `connect`
  goes through `claude mcp remove` and `claude mcp add --transport http`.

## One server per project

OpenKnowledge holds one server per project through `.ok/local/server.lock` in the vault. A second
`ok start` for the vault finds the running one and leaves it ("already running on this project"),
so `ok open`, `ok status` and `ok audit` from the vault use the shared server.

## What it does not cover

- **Sessions started inside the vault** use the vault's committed `.mcp.json`, a project-scope
  stdio entry that wins over the user-scope one. Those still start their own copy.
- **Other agents** (Codex, OpenCode, Antigravity) keep whatever entry they were given.
- **Running sessions** keep the connector they started with until they restart.
- **The OK Desktop app** is not installed here. It would find the vault's lock held by this
  server; whether it attaches to a server it did not start has not been tested. If you install it,
  check `ok status` from the vault and the app together before relying on both.

If `ok init` or the Desktop app rewrites the user-scope entry back to stdio, `status` reports the
`claude entry` row as failed; run `connect --apply` again.
