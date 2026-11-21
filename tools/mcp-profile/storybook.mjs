// Optional docs-only companion. Dependencies are installed separately; never started by rig jobs.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';

const { values } = parseArgs({ options: {
  module: { type: 'string' }, manifests: { type: 'string' },
  port: { type: 'string', default: '18093' }, 'idle-seconds': { type: 'string', default: '300' },
  help: { type: 'boolean' },
} });
if (values.help) {
  console.log(`Docs-only, loopback Storybook MCP with idle shutdown; Node 22+.
  --module tools/mcp-profile/storybook/node_modules/@storybook/mcp/dist/index.js
  --manifests /absolute/path/to/storybook-static/manifests
  --port 18093 --idle-seconds 300
Install separately: npm ci --prefix tools/mcp-profile/storybook --ignore-scripts
Build your Storybook manifests first. No Vite, browser, indexer or background service is started.
HTTP clients reconnect after restarting this command following idle shutdown.`);
  process.exit(0);
}
if (!values.module || !values.manifests) throw new Error('--module and --manifests are required');
const port = Number(values.port), idleSeconds = Number(values['idle-seconds']);
if (!Number.isInteger(port) || port < 1 || port > 65535 || !Number.isInteger(idleSeconds) || idleSeconds < 1)
  throw new Error('Invalid port or idle timeout');
const manifests = resolve(values.manifests);
// Refuse to advertise a docs server when there is no generated component catalogue.
JSON.parse(await readFile(join(manifests, 'components.json'), 'utf8'));
const { createStorybookMcpHandler } = await import(pathToFileURL(resolve(values.module)).href);
const handler = await createStorybookMcpHandler({
  manifestProvider: async (_request, path) => {
    if (path !== './manifests/components.json' && path !== './manifests/docs.json')
      throw new Error(`Unsupported manifest: ${path}`);
    return readFile(join(manifests, path.slice('./manifests/'.length)), 'utf8');
  },
});
let active = 0, lastActivity = performance.now();
const server = createServer(async (incoming, outgoing) => {
  // An idle SSE subscription must not keep the companion alive forever.
  const tracked = incoming.method !== 'GET';
  if (tracked) active++;
  try {
    if (incoming.url !== '/mcp') { outgoing.writeHead(404).end(); return; }
    if (incoming.headers.origin) {
      const origin = new URL(incoming.headers.origin);
      if (origin.hostname !== '127.0.0.1' && origin.hostname !== 'localhost') { outgoing.writeHead(403).end(); return; }
    }
    const method = incoming.method ?? 'GET';
    const body = [];
    let size = 0;
    for await (const chunk of incoming) {
      size += chunk.length;
      if (size > 1024 * 1024) { outgoing.writeHead(413).end(); return; }
      body.push(chunk);
    }
    const request = new Request(`http://127.0.0.1:${port}/mcp`, {
      method, headers: incoming.headers,
      ...(!['GET', 'HEAD'].includes(method) ? { body: Buffer.concat(body) } : {}),
    });
    const response = await handler(request);
    outgoing.writeHead(response.status, Object.fromEntries(response.headers));
    if (response.body) await pipeline(Readable.fromWeb(response.body), outgoing);
    else outgoing.end();
  } catch (error) {
    console.error(error.message);
    if (!outgoing.headersSent) outgoing.writeHead(500).end();
    else outgoing.destroy();
  } finally { if (tracked) { active--; lastActivity = performance.now(); } }
});
server.on('error', error => { console.error(error.message); process.exitCode = 1; clearInterval(timer); });
const timer = setInterval(() => {
  if (active === 0 && performance.now() - lastActivity >= idleSeconds * 1000) {
    console.error('Storybook MCP idle timeout; stopping');
    clearInterval(timer); server.close(); server.closeAllConnections();
  }
}, Math.min(1000, idleSeconds * 1000));
server.listen(port, '127.0.0.1', () => console.error(`Storybook MCP: http://127.0.0.1:${port}/mcp; idle timeout ${idleSeconds}s`));
