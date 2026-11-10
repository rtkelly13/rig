#!/usr/bin/env node
// Claude Code SessionStart hook: runs the session at low CPU priority, so its builds and
// tests never starve the user's foreground work.
//
// Finds the `claude` process this hook runs under and sets it to below-normal priority
// (nice 10 on macOS and Linux, BELOW_NORMAL on Windows), along with the processes it has
// already started (MCP servers). Priority is inherited, so every shell, build and test
// the session starts afterwards runs low too. A priority that is already lower is kept.
// It then prints a line of session context asking for light local builds and CI for the
// heavy ones.
//
// Opt out for one session: CLAUDE_FOREGROUND=1 claude
//
// Installed and wired by the devenv Claude-settings step
// (src/rig/devenv/steps/ai_tools/claude_code.py). Never fails the session: every error
// path exits 0 silently. Only the native `claude` binary is recognised; a session run
// through `node cli.js` is left alone.

import { execFileSync } from "node:child_process";
import { constants, getPriority, setPriority } from "node:os";
import { pathToFileURL } from "node:url";

export const TARGET_PRIORITY = constants.priority.PRIORITY_BELOW_NORMAL;
const MAX_DEPTH = 6;

export const CONTEXT =
  "This machine is shared with the user's foreground work and this session runs at low CPU " +
  "priority. Keep local builds and tests light: build/test only the affected project, limit " +
  "parallelism (dotnet: -m:2, pytest: no -n auto), and avoid full-solution builds or whole " +
  "test suites locally. Prefer pushing and letting GitHub CI run heavy checks; slower is fine.";

const WINDOWS_TABLE =
  "Get-CimInstance Win32_Process | ForEach-Object { $_.ProcessId, $_.ParentProcessId, $_.Name -join ' ' }";

/** Parse "pid ppid name" lines into a Map of pid -> { ppid, name }. */
export function parseProcessTable(text) {
  const table = new Map();
  for (const line of text.split(/\r?\n/)) {
    const match = /^\s*(\d+)\s+(\d+)\s+(.+?)\s*$/.exec(line);
    if (match) table.set(Number(match[1]), { ppid: Number(match[2]), name: match[3] });
  }
  return table;
}

/** The executable's own name: no directory, and on Windows no `.exe` and no case. */
export function processName(command, platform = process.platform) {
  const name = command.split(/[\\/]/).pop() ?? "";
  return platform === "win32" ? name.toLowerCase().replace(/\.exe$/, "") : name;
}

/** Walk up from `startPid` to the nearest `claude` process; null when there is none. */
export function findClaude(table, startPid, platform = process.platform) {
  let pid = startPid;
  for (let depth = 0; depth < MAX_DEPTH && pid > 1; depth++) {
    const entry = table.get(pid);
    if (!entry) return null;
    if (processName(entry.name, platform) === "claude") return pid;
    pid = entry.ppid;
  }
  return null;
}

export function childrenOf(table, pid) {
  return [...table].filter(([, entry]) => entry.ppid === pid).map(([child]) => child);
}

function readProcessTable() {
  const options = { encoding: "utf8", timeout: 8000, windowsHide: true, stdio: ["ignore", "pipe", "ignore"] };
  const text =
    process.platform === "win32"
      ? execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", WINDOWS_TABLE], options)
      : execFileSync("ps", ["-A", "-o", "pid=,ppid=,comm="], options);
  return parseProcessTable(text);
}

function lower(pid) {
  try {
    if (getPriority(pid) < TARGET_PRIORITY) setPriority(pid, TARGET_PRIORITY);
  } catch {
    // Gone already, or not ours to change.
  }
}

function main() {
  if (process.env.CLAUDE_FOREGROUND === "1") return;
  const table = readProcessTable();
  const claude = findClaude(table, process.ppid);
  if (claude === null) return;
  for (const pid of [claude, ...childrenOf(table, claude)]) lower(pid);
  process.stdout.write(CONTEXT + "\n");
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    main();
  } catch {
    // A hook must never break session start.
  }
}
