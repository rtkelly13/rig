#!/usr/bin/env node
// Claude Code SessionEnd hook: records how each session ended.
//
// Writes ~/.claude/session-ends/<session_id>.json with the hook's reason
// ("clear" for /clear, "prompt_input_exit", "logout", "other", ...). A crash or
// a killed process fires no hook, and a failed write is swallowed, so a missing
// record only means the ending was not recorded. Session-recovery tooling reads
// an explicit `reason: "clear"` as "finished".
//
// Installed and wired by the devenv Claude-settings step
// (src/devenv_setup/steps/claude_settings.py). Never fails the session: every
// error path exits 0 silently, inside the SessionEnd time budget.

import { mkdirSync, renameSync, rmSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const SESSION_ID = /^[A-Za-z0-9_-]{1,128}$/;

function buildRecord(input, now) {
  if (!input || typeof input !== "object") return null;
  const id = input.session_id;
  if (typeof id !== "string" || !SESSION_ID.test(id)) return null;
  const text = (value) => (typeof value === "string" ? value : null);
  return {
    session_id: id,
    reason: text(input.reason),
    cwd: text(input.cwd),
    transcript_path: text(input.transcript_path),
    ended_at: now.toISOString(),
  };
}

function writeRecord(dir, record) {
  mkdirSync(dir, { recursive: true });
  const target = join(dir, `${record.session_id}.json`);
  const staging = `${target}.${process.pid}.tmp`;
  try {
    writeFileSync(staging, JSON.stringify(record, null, 2) + "\n", "utf8");
    renameSync(staging, target);
  } catch (error) {
    rmSync(staging, { force: true });
    throw error;
  }
}

async function readStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
}

try {
  const record = buildRecord(JSON.parse(await readStdin()), new Date());
  const dir =
    process.env.CLAUDE_SESSION_LEDGER_DIR || join(homedir(), ".claude", "session-ends");
  if (record) writeRecord(dir, record);
} catch {
  // A hook must never break session shutdown.
}
process.exit(0);
