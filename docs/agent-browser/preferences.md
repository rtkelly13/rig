# agent-browser preferences

Personal preferences for agent-browser sessions. Only covers things not
discoverable from `agent-browser --help`.

## Profile mapping

| Profile | Use for |
|:--------|:--------|
| **dev** (default) | GitHub, localhost, personal projects, self-hosted tools, dev dashboards |
| **dev**, explicitly selected | ChatGPT Work scheduled development tasks; use `~/.agent-browser-profiles/dev`, never daily Chrome or auto-connect ([workflow](chatgpt-work-tasks.md)) |
| **personal** | Banking, finance, shopping, streaming, Google/email, anything requiring personal accounts |

When in doubt: if it involves a personal account that has nothing to do with
code or tooling, use `personal`. If it involves a service you'd use while
working, use `dev`.

## Session naming

Use kebab-case, `<verb>-<subject>` format:

```
verify-page      # smoke-checking a generated page
scrape-<site>    # extracting data from a specific site
fill-<form>      # completing a form flow
auth-<service>   # one-time sign-in run
```

Keep names short. Use `--session` for any task that involves more than one
command so the daemon state is clearly scoped.

## Startup

Run `agent-browser close --all` before starting a **new task**, not between
commands within the same task. Do not carry daemon state across unrelated sessions.
