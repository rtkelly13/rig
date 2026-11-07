# agent-browser

Browser automation CLI for AI agents by Vercel Labs. Used in this repo for
automating personal projects, development workflows, and personal services.

- **Install:** `npm install -g agent-browser` then `agent-browser install` (downloads Chromium)
- **Reference:** `agent-browser --help` or `agent-browser skills get core --full`
- **Setup:** `uv run rig browser setup` (provisions user-scoped env vars — run once per machine)

## Guides

| Guide | When to use |
|:------|:------------|
| [connect.md](connect.md) | Choosing a connection approach — dedicated profile vs auto-connect vs debug-port |
| [preferences.md](preferences.md) | Profile-to-site mapping, session naming, startup pattern |
| [chatgpt-work-tasks.md](chatgpt-work-tasks.md) | Manage ChatGPT Work cloud schedules in the dedicated dev browser; publish and read fixed GitHub review issues |
| [../project-reviews/README.md](../project-reviews/README.md) | Live per-schedule policies on main, shared continuation contract and saved bootstrap |
| [weekly-project-review-prompt.md](weekly-project-review-prompt.md) | Historical full-baseline report example |
| [project-review-rotation.md](project-review-rotation.md) | Native project groups, target-state plan/apply/audit, active memory and provider verification |
| [project-context-template.md](project-context-template.md) | The review context saved in each managed ChatGPT Project |
| [five-review-groups.md](five-review-groups.md) | Four active groups, each weekly on Monday/Tuesday/Thursday/Friday |

---

## Two dedicated profiles

Two **agent-browser-only** Chrome profiles are defined for this repo. Both are
separate user-data directories, completely distinct from your daily Chrome —
never locked, safe to drive while your regular browser is open. Sign into the
relevant services in each **once**; the session persists.

| Profile | Directory | Use for |
|:--------|:----------|:--------|
| **dev** (default) | `~/.agent-browser-profiles/dev` | Development automation: localhost servers, personal projects, GitHub, self-hosted tooling |
| **personal** | `~/.agent-browser-profiles/personal` | Personal services unrelated to development: banking, shopping, subscriptions, etc. |

> **Do not point `--profile` at your daily Chrome profile dir.** Chrome locks
> that dir while it is open, so agent-browser silently falls back to a fresh,
> logged-out Chromium. The dedicated dirs above sidestep this entirely.

### One-time sign-in

```bash
# Sign into the dev profile (headed window opens; complete sign-in, then close)
agent-browser --profile ~/.agent-browser-profiles/dev --headed open <url>

# Sign into the personal profile
agent-browser --profile ~/.agent-browser-profiles/personal --headed open <url>
```

---

## Setup (run once per machine)

`uv run rig browser setup` provisions three user-scoped environment variables
(never-clobber — anything already set is left untouched):

| Variable | Value | Role |
|:---------|:------|:-----|
| `AGENT_BROWSER_PROFILE` | `~/.agent-browser-profiles/dev` | Default profile — the dev profile |
| `AGENT_BROWSER_AUTO_CONNECT` | `0` | Keep auto-connect opt-in (reuse daily Chrome only when you ask) |
| `AGENT_BROWSER_HEADED` | `1` | Run headed by default |

```bash
uv run rig browser setup       # dry-run first: uv run rig browser setup --dry-run
```

Open a new terminal after setup for the env vars to take effect.

> **Daemon restart.** Profile and headed flags bind only at daemon startup.
> If a daemon is already running when you run `setup`, it keeps its old profile
> until restarted: `agent-browser close --all`.

### Switching profiles

`AGENT_BROWSER_PROFILE` is the only stored default. To change it for good, set
the variable (or rerun `uv run rig browser setup --force` after editing it).
`devenv` also installs `agent-browser-dev` / `agent-browser-personal` shell
shortcuts that pass `--profile <dir>` for one command.

For a single run, override inline:

```bash
# Use the personal profile for this run only
AGENT_BROWSER_PROFILE=~/.agent-browser-profiles/personal agent-browser open <url>
```

---

## Choosing a connection approach

See **[connect.md](connect.md)** for the full comparison. In brief:

| You're automating… | Use | Why |
|:--------------------|:----|:----|
| **Personal projects / development work** — localhost, GitHub, self-hosted apps | **dev profile** (default) | Stable, owned by agent-browser, signed in once |
| **Personal services** — banking, shopping, etc. | **personal profile** | Keeps sessions separate from dev work |
| **Ad-hoc check on a third-party site you're already logged into** | `--auto-connect` | Reuses your running Chrome's live session |

---

## Core workflow

```bash
# 1. Open URL — uses the default profile (dev, from AGENT_BROWSER_PROFILE)
agent-browser open <url>

# 2. Get interactive elements with refs
agent-browser snapshot -i

# 3. Interact by ref
agent-browser click @e1
agent-browser fill @e2 "text"
agent-browser press Enter

# 4. Re-snapshot after every navigation — refs are invalidated on page change
agent-browser snapshot -i
```

Safe verification pattern (for quick smoke-checks):

```bash
agent-browser --session verify-page close
agent-browser --session verify-page open <url>
agent-browser --session verify-page wait 1500
agent-browser --session verify-page screenshot temp/verify.png
agent-browser --session verify-page get title
agent-browser --session verify-page get url
agent-browser --session verify-page get text h1
agent-browser --session verify-page close
```

---

## Key behaviours and gotchas

- **Refs are per-snapshot.** `@e1`, `@e2` etc. are invalidated after any
  navigation or DOM mutation. Always re-snapshot before acting on a new page.
- **`snapshot -i` for agents.** The `-i` flag returns only interactive elements.
  Use it by default — the full tree is verbose.
- **Daemon is persistent.** The browser stays alive between commands.
  `agent-browser close --all` resets everything including profile and headed state.
- **`close` while auto-connected can close your real Chrome.** When attached via
  `--auto-connect`, agent-browser drives your actual browser over CDP — `close --all`
  may shut your live Chrome and its tabs. Prefer `tab close` for a single tab.
- **`open` can hang after success.** Especially with `file://` URLs. Use a
  10–15 s timeout and treat a timeout as success if the page title was printed.
- **`fill` appends, `type` often doesn't.** In rich text editors, `fill`
  reliably appends text while `type` may silently fail. Prefer `fill` for
  contenteditable fields.
- **Avoid high-output verification by default.** For routine checks, prefer
  `screenshot`, `get title`, `get url` over `snapshot` — a large snapshot can
  look like a stall in some shells.
- **Explicitly close short-lived sessions.** Finish verification flows with
  `agent-browser --session <name> close` so the lifecycle is obvious and
  the command returns cleanly.

---

## Setup reference

```bash
uv run rig browser setup [--dry-run] [--force]   # provision env vars
```

Everything else is the upstream `agent-browser` CLI (`agent-browser --help`).
