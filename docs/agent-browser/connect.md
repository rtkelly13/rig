# Connection approaches — choosing how to drive the browser

agent-browser can drive a browser three ways. The right one depends on **what
you're automating**. Pick using the table below, then follow the matching recipe.

> **For development work and personal projects** (localhost, GitHub, self-hosted
> apps, personal services), use a **dedicated profile** — its own data dir,
> signed in once. The **dev** profile is the machine default (`AGENT_BROWSER_PROFILE`); switch to
> **personal** for personal-service sessions. Both are the most stable option
> because agent-browser owns those Chrome instances exclusively (no attach
> banner, no active-tab drift, no background-tab throttling) and they work
> while your normal Chrome is open.

> **Two different "profiles."** A *dedicated* profile (approach A) is a
> **separate data directory** — never locked, safe while Chrome is open.
> Pointing `--profile` at your **daily-driver** Chrome profile dir is different:
> Chrome locks it while running, so agent-browser silently launches a fresh,
> **logged-out** Chromium (private URLs 404). The dedicated dirs sidestep
> that lock entirely.

| Approach | Reuses a **running** Chrome's session? | Login | Best for |
|:---------|:---------------------------------------|:------|:---------|
| **A. Dedicated profile** (`dev` or `personal`) (default) | N/A — owns a dedicated Chrome on its own data dir | Sign in once; persists | Development automation and personal services — the most stable path |
| **B. `--auto-connect`** | **Yes** — auto-discovers a running debuggable Chrome and attaches via CDP | Reuses your live session | Ad-hoc checks against a third-party site already open in your Chrome |
| **C. `--cdp <port>` / `connect <port>`** | **Yes** — attaches to a specific debug port | Log in fresh in the debug window | One-off runs against a known debug-port Chrome |

---

## A. Dedicated profile — dev or personal

The recommended, most stable path. agent-browser launches and owns a Chrome for
Testing against a persistent data dir that is **separate from your daily
Chrome** — no attach banner, no active-tab drift, no background-tab throttling,
no profile-lock conflict.

### dev profile (default)

For development automation: localhost servers, personal projects, GitHub,
self-hosted tooling.

```bash
# One-time: sign in (headed window opens; complete sign-in, then close)
agent-browser --profile ~/.agent-browser-profiles/dev --headed open <url>

# Thereafter — the session persists:
agent-browser open <url>          # uses dev profile (AGENT_BROWSER_PROFILE)
agent-browser snapshot -i
```

`rig browser setup` provisions `AGENT_BROWSER_PROFILE=~/.agent-browser-profiles/dev`
as the machine default, so the bare `agent-browser open <url>` uses it.

### personal profile

For personal services unrelated to development (banking, shopping, subscriptions).
Sign in separately so sessions stay isolated from dev work.

```bash
# One-time: sign in
agent-browser --profile ~/.agent-browser-profiles/personal --headed open <url>

# Per-run override (without changing the machine default):
AGENT_BROWSER_PROFILE=~/.agent-browser-profiles/personal agent-browser open <url>

# Or the shortcut devenv installs:
agent-browser-personal open <url>
```

> **The env var wins over the flag.** If `AGENT_BROWSER_AUTO_CONNECT=1` is set
> it overrides `--profile`. Prefix the invocation with `AGENT_BROWSER_AUTO_CONNECT=0`
> to force the dedicated profile.

---

## B. `--auto-connect` — reuse a running third-party session

When you need a **third-party site's live session** that you're already signed
into in your daily Chrome, auto-connect attaches to that running Chrome and
reuses it — no fresh sign-in. Since the dedicated profile is the machine
default, opt in per-invocation:

```bash
AGENT_BROWSER_AUTO_CONNECT=1 agent-browser open <url>
AGENT_BROWSER_AUTO_CONNECT=1 agent-browser snapshot -i
```

> **When no Chrome is running, auto-connect hard-errors** ("No running Chrome
> instance found") — it does **not** launch its own browser. For automation
> that shouldn't depend on your daily Chrome being open, use a dedicated profile
> (approach A) instead.

> **`close` while auto-connected can close your real Chrome.** `close --all`
> may shut your live Chrome and all its tabs. Prefer `tab close` for a
> single tab.

### Capturing auth for headless replay

```bash
AGENT_BROWSER_AUTO_CONNECT=1 agent-browser state save ./my-auth.json
agent-browser --state ./my-auth.json open <url>
```

---

## C. `--cdp <port>` — attach to a specific debug-port Chrome

When you know the debug port, attach explicitly. Launch a *separate* Chrome
instance with a debug port and its own data dir (isolated from your main Chrome),
log in there, then attach:

```bash
# macOS — launch debug Chrome
"/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
  --remote-debugging-port=9222 \
  --user-data-dir=~/.chrome-agent-debug \
  --no-first-run

# Attach (either form)
agent-browser --cdp 9222 snapshot -i
agent-browser connect 9222
```

`connect` accepts a port, a `ws://` URL, or a remote `wss://` endpoint.
Find the WebSocket URL at `http://localhost:9222/json/version`.
