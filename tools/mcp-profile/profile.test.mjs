import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, mkdir, rm } from 'node:fs/promises';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';
import { resolve, join } from 'node:path';
import { createServer } from 'node:net';

const root = resolve(import.meta.dirname, '../..');
await mkdir(join(root, 'temp'), { recursive: true });

test('profiler exchanges MCP, calls tools and observes clean EOF shutdown', async () => {
  const dir = await mkdtemp(join(root, 'temp/mcp-profile-test-'));
  try {
    const fixture = join(dir, 'stdio.mjs');
    await writeFile(fixture, `import { createInterface } from 'node:readline';
const input = createInterface({input:process.stdin});
input.on('line', line => { const m=JSON.parse(line); if(!m.id)return;
const result=m.method==='initialize'?{protocolVersion:'2025-06-18',serverInfo:{name:'fixture',version:'1'}}:
m.method==='tools/list'?{tools:[{name:'read'}]}:{content:[{type:'text',text:'ok'}]};
console.log(JSON.stringify({jsonrpc:'2.0',id:m.id,result})); });`);
    const child = spawn(process.execPath, [join(root, 'tools/mcp-profile/profile.mjs'), '--command',
      JSON.stringify([process.execPath, fixture]), '--tools', 'read', '--runs', '1', '--idle', '1', '--calls', '3']);
    let output = '', error = '';
    child.stdout.on('data', x => { output += x; }); child.stderr.on('data', x => { error += x; });
    const [code] = await once(child, 'exit');
    assert.equal(code, 0, error);
    const sample = JSON.parse(output).samples[0];
    assert.equal(sample.startup[0].server.name, 'fixture');
    assert.equal(sample.busy.totalCalls, 3);
    assert.ok(sample.idle.cpuPercent >= 0);
    assert.ok(sample.idle.rssMiB[0] > 0);
    assert.equal(sample.shutdown[0].exitCode, 0);
    assert.deepEqual(sample.remainingPids, []);
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('companion refuses foreign origins, serves manifests and expires despite an idle SSE stream', async () => {
  const dir = await mkdtemp(join(root, 'temp/mcp-profile-test-'));
  let child, reader;
  try {
    const socket = createServer(); socket.listen(0, '127.0.0.1'); await once(socket, 'listening');
    const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
    const module = join(dir, 'handler.mjs');
    await writeFile(join(dir, 'components.json'), '{"v":0,"components":{}}');
    await writeFile(module, `export async function createStorybookMcpHandler({manifestProvider}) {
return async req => req.method==='GET'?new Response(new ReadableStream({start(c){c.enqueue(new TextEncoder().encode('data: {}\\n\\n'));}}),{headers:{'Content-Type':'text/event-stream'}}):
new Response(await manifestProvider(req,'./manifests/components.json'),{headers:{'Content-Type':'application/json'}}); }`);
    child = spawn(process.execPath, [join(root, 'tools/mcp-profile/storybook.mjs'), '--module', module,
      '--manifests', dir, '--port', String(port), '--idle-seconds', '1']);
    let error = ''; child.stderr.on('data', x => { error += x; });
    const exited = once(child, 'exit');
    const url = `http://127.0.0.1:${port}/mcp`;
    for (let i = 0;; i++) {
      try { const r = await fetch(url, { method: 'OPTIONS' }); await r.body?.cancel(); break; }
      catch (e) { if (i > 100 || child.exitCode !== null) throw new Error(error, { cause: e }); await delay(20); }
    }
    const refused = await fetch(url, { method: 'POST', headers: { Origin: 'https://example.com' } });
    assert.equal(refused.status, 403); await refused.body?.cancel();
    const result = await fetch(url, { method: 'POST' });
    assert.deepEqual(await result.json(), { v: 0, components: {} });
    const response = await fetch(url); reader = response.body.getReader();
    assert.ok((await reader.read()).value.length > 0);
    const [code] = await Promise.race([exited, delay(5000).then(() => { throw new Error('Idle SSE kept server alive'); })]);
    assert.equal(code, 0, error);
    assert.match(error, /idle timeout; stopping/);
  } finally {
    await reader?.cancel().catch(() => {});
    child?.kill('SIGKILL');
    await rm(dir, { recursive: true, force: true });
  }
});
