import { spawn, execFileSync } from 'node:child_process';
import { createInterface } from 'node:readline';
import { parseArgs } from 'node:util';
import { setTimeout as delay } from 'node:timers/promises';
import os from 'node:os';

const { values } = parseArgs({ options: {
  command: { type: 'string' }, url: { type: 'string' }, pid: { type: 'string' },
  tools: { type: 'string', default: 'disk_usage,list_jobs,list_runs,decisions,attention' },
  runs: { type: 'string', default: '3' }, copies: { type: 'string', default: '1' }, clients: { type: 'string', default: '1' },
  idle: { type: 'string', default: '5' }, calls: { type: 'string', default: '100' },
  help: { type: 'boolean' },
} });
if (values.help) {
  console.log(`Profile an MCP server on macOS/Linux; JSON report on stdout.
  node tools/mcp-profile/profile.mjs --command '["dotnet","src/Rig.Cli/bin/Debug/net11.0/rig.dll","web","mcp"]'
  --url http://127.0.0.1:8090/api/mcp --pid <existing-host-pid>
  --command '["dotnet",".../rig.dll","web","--port","18092","--no-open"]' --url http://127.0.0.1:18092/api/mcp
  --copies 4 (stdio) --clients 4 (HTTP) --runs 3 --idle 5 --calls 100 --tools disk_usage,list_jobs
Commands are argv JSON, never shell text. Only named tools with empty arguments are called.
Use read-only tools. --tools '' measures discovery only. Existing --pid hosts are never stopped.
CPU is delta process-tree CPU time / wall time (100% = one core); RSS is summed KiB / 1024.
Fresh processes use warm filesystem/package caches; this does not simulate a reboot.`);
  process.exit(0);
}
if (process.platform === 'win32') throw new Error('ps sampling requires macOS or Linux');
const argv = values.command ? JSON.parse(values.command) : null;
if ((!argv && !values.url) || (argv && (!Array.isArray(argv) || !argv.length || argv.some(x => typeof x !== 'string'))))
  throw new Error('Supply --command as argv JSON, or --url with optional --pid');
for (const key of ['runs', 'copies', 'clients', 'idle', 'calls']) {
  if (!Number.isInteger(Number(values[key])) || Number(values[key]) < 1) throw new Error(`Invalid --${key}`);
}
if (values.url && Number(values.copies) !== 1) throw new Error('HTTP sharing uses one host; use separate clients against it');
if (values.pid && (!Number.isInteger(Number(values.pid)) || Number(values.pid) < 1 || argv)) throw new Error('--pid must name an existing host, without --command');
if (values.url && !argv && !values.pid) throw new Error('Existing HTTP hosts require --pid for footprint sampling');
if (!values.url && Number(values.clients) !== 1) throw new Error('--clients is for HTTP; use --copies for stdio');
const tools = values.tools.split(',').filter(Boolean);
const round = n => Math.round(n * 1000) / 1000;
function snapshot(roots, cpuLedger = new Map()) {
  const rows = execFileSync('ps', ['-axo', 'pid=,ppid=,pgid=,rss=,time='], { encoding: 'utf8' })
    .trim().split('\n').map(line => {
      const [pid, ppid, pgid, rss, time] = line.trim().split(/\s+/);
      const [days, clock] = time.includes('-') ? time.split('-') : ['0', time];
      const seconds = clock.split(':').reduce((sum, part) => sum * 60 + Number(part), 0) + Number(days) * 86400;
      return { pid: Number(pid), ppid: Number(ppid), pgid: Number(pgid), rss: Number(rss), seconds };
    });
  const included = new Set(roots);
  for (const row of rows) if (roots.includes(row.pgid)) included.add(row.pid);
  let old;
  do { old = included.size; for (const row of rows) if (included.has(row.ppid)) included.add(row.pid); } while (old !== included.size);
  const tree = rows.filter(row => included.has(row.pid));
  for (const row of tree) cpuLedger.set(row.pid, Math.max(cpuLedger.get(row.pid) ?? 0, row.seconds));
  return { rssMiB: round(tree.reduce((sum, row) => sum + row.rss, 0) / 1024),
    cpuSeconds: [...cpuLedger.values()].reduce((sum, seconds) => sum + seconds, 0), pids: tree.map(row => row.pid) };
}
function client(child, url) {
  let id = 0, session, terminalError, version = '2025-06-18';
  const pending = new Map();
  if (!url) {
    const fail = error => { terminalError = error; for (const waiter of pending.values()) waiter.reject(error); pending.clear(); };
    const lines = createInterface({ input: child.stdout });
    lines.on('line', line => {
      try { const message = JSON.parse(line); const waiter = pending.get(message.id); if (waiter) { pending.delete(message.id); waiter.resolve(message); } }
      catch (error) { fail(error); }
    });
    child.on('exit', () => fail(new Error('Server exited')));
    child.on('error', fail);
    child.stdin.on('error', fail);
  }
  async function send(method, params, notification = false) {
    if (terminalError) throw terminalError;
    const message = { jsonrpc: '2.0', ...(!notification ? { id: ++id } : {}), method, params };
    let answer;
    if (url) {
      const headers = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream', 'MCP-Protocol-Version': version };
      if (session) headers['Mcp-Session-Id'] = session;
      const response = await fetch(url, { method: 'POST', headers, body: JSON.stringify(message), signal: AbortSignal.timeout(30000) });
      if (!response.ok) throw new Error(`HTTP ${response.status}: ${await response.text()}`);
      session = response.headers.get('mcp-session-id') ?? session;
      if (notification) { await response.body?.cancel(); return; }
      if (response.headers.get('content-type')?.includes('text/event-stream')) {
        const reader = response.body.getReader(); const decoder = new TextDecoder(); let buffer = '';
        try {
          while (!answer) {
            const { value, done } = await reader.read(); if (done) throw new Error('SSE ended before response');
            buffer += decoder.decode(value, { stream: true }).replaceAll('\r\n', '\n');
            let end;
            while ((end = buffer.indexOf('\n\n')) >= 0) {
              const event = buffer.slice(0, end); buffer = buffer.slice(end + 2);
              const data = event.split('\n').filter(x => x.startsWith('data:')).map(x => x.slice(5).trimStart()).join('\n');
              if (data) { const parsed = JSON.parse(data); if (parsed.id === message.id) answer = parsed; }
            }
          }
        } finally { await reader.cancel(); }
      } else answer = await response.json();
    } else {
      if (notification) { child.stdin.write(JSON.stringify(message) + '\n'); return; }
      answer = await new Promise((resolve, reject) => {
        const timer = setTimeout(() => { pending.delete(message.id); reject(new Error(`Timed out: ${method}`)); }, 30000);
        pending.set(message.id, { resolve: x => { clearTimeout(timer); resolve(x); }, reject: e => { clearTimeout(timer); reject(e); } });
        child.stdin.write(JSON.stringify(message) + '\n');
      });
    }
    if (answer.error || answer.result?.isError) throw new Error(`MCP failure: ${JSON.stringify(answer)}`);
    return answer.result;
  }
  return { send, async initialize() {
    const result = await send('initialize', { protocolVersion: version, capabilities: {}, clientInfo: { name: 'rig-profile', version: '1' } });
    version = result.protocolVersion;
    await send('notifications/initialized', {}, true);
    return result.serverInfo;
  }, async close() {
    if (url && session) await fetch(url, { method: 'DELETE', headers: { 'Mcp-Session-Id': session, 'MCP-Protocol-Version': version }, signal: AbortSignal.timeout(5000) }).then(r => r.body?.cancel());
  } };
}
const report = { at: new Date().toISOString(), platform: `${os.platform()} ${os.release()} ${os.arch()}`, cpu: os.cpus()[0].model,
  ramGiB: os.totalmem() / 1024 ** 3, node: process.version, argv, url: values.url, options: values, samples: [] };
for (let run = 0; run < Number(values.runs); run++) {
  const children = [], clients = [], roots = values.pid ? [Number(values.pid)] : [], cpuLedger = new Map();
  try {
    const startup = [];
    for (let copy = 0; copy < Number(values.url ? values.clients : values.copies); copy++) {
      const start = performance.now();
      const child = argv && (!values.url || copy === 0) ? spawn(argv[0], argv.slice(1), { stdio: ['pipe', 'pipe', 'pipe'], detached: true }) : null;
      if (child) {
        children.push(child); roots.push(child.pid);
        child.on('error', error => console.error(error.message));
        child.stderr.on('data', data => process.stderr.write(data));
        if (values.url) child.stdout.resume();
        if (!child.pid) throw new Error(`Could not start ${argv[0]}`);
      }
      if (values.url && child) {
        for (;;) {
          try { await fetch(values.url, { method: 'OPTIONS', signal: AbortSignal.timeout(500) }); break; }
          catch { if (child.exitCode !== null || performance.now() - start > 30000) throw new Error('Host did not listen'); await delay(25); }
        }
      }
      const connection = client(child, values.url); clients.push(connection);
      const server = await connection.initialize();
      const initializedMs = performance.now() - start;
      const listed = await connection.send('tools/list', {});
      for (const tool of tools) if (!listed.tools.some(x => x.name === tool)) throw new Error(`Missing tool ${tool}; available: ${listed.tools.map(x => x.name).join(', ')}`);
      startup.push({ initializedMs: round(initializedMs), readyMs: round(performance.now() - start), server, toolCount: listed.tools.length });
    }
    // Let initialization/JIT settle, then use CPU deltas, not ps lifetime %CPU.
    await delay(1000);
    const before = snapshot(roots, cpuLedger), idleStart = performance.now(), idleRss = [];
    if (roots.some(pid => !before.pids.includes(pid))) throw new Error('A measured server exited before idle sampling');
    for (let second = 0; second < Number(values.idle); second++) { await delay(1000); idleRss.push(snapshot(roots, cpuLedger).rssMiB); }
    const idleEnd = snapshot(roots, cpuLedger), idleMs = performance.now() - idleStart;
    if (roots.some(pid => !idleEnd.pids.includes(pid))) throw new Error('A measured server exited during idle sampling');
    const latencies = [], busyStart = performance.now();
    for (let call = 0; tools.length && call < Number(values.calls); call++) {
      const start = performance.now();
      await Promise.all(clients.map(c => c.send('tools/call', { name: tools[call % tools.length], arguments: {} })));
      latencies.push(performance.now() - start);
    }
    const busyEnd = snapshot(roots, cpuLedger), busyMs = performance.now() - busyStart;
    const sorted = [...latencies].sort((a, b) => a - b);
    for (const connection of clients) await connection.close();
    const shutdownStart = performance.now(), shutdown = [];
    for (const child of children) {
      if (values.url) child.kill('SIGTERM'); else child.stdin.end();
      while (child.exitCode === null && child.signalCode === null && performance.now() - shutdownStart < 5000) await delay(10);
      shutdown.push({ exitCode: child.exitCode, signal: child.signalCode, stopped: child.exitCode !== null || child.signalCode !== null });
    }
    report.samples.push({ startup, before, idle: { rssMiB: idleRss, wallMs: round(idleMs), cpuSeconds: round(idleEnd.cpuSeconds - before.cpuSeconds), cpuPercent: round((idleEnd.cpuSeconds - before.cpuSeconds) / idleMs * 100000) },
      busy: { rounds: latencies.length, totalCalls: latencies.length * clients.length, wallMs: round(busyMs), rssMiB: busyEnd.rssMiB,
        cpuSeconds: round(busyEnd.cpuSeconds - idleEnd.cpuSeconds), cpuPercent: round((busyEnd.cpuSeconds - idleEnd.cpuSeconds) / busyMs * 100000), medianMs: round(sorted[Math.floor(sorted.length / 2)] ?? 0), p95Ms: round(sorted[Math.max(0, Math.ceil(sorted.length * .95) - 1)] ?? 0) },
      shutdown, shutdownMs: round(performance.now() - shutdownStart), remainingPids: snapshot(roots).pids });
    console.error(`Run ${run + 1}/${values.runs} complete`);
  } finally {
    for (const child of children) { if (child.pid) { try { process.kill(-child.pid, 'SIGKILL'); } catch (error) { if (error.code !== 'ESRCH') throw error; } } }
  }
}
console.log(JSON.stringify(report, null, 2));
