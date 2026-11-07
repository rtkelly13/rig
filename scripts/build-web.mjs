import { build } from 'esbuild';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { readFile, mkdir, copyFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const checking = process.argv.includes('--check');
const committed = path.join(root, 'src/rig/image_drift/templates/app_assets');
const output = checking ? path.join(root, 'temp/web-build-check') : committed;
await mkdir(output, { recursive: true });
await build({
  entryPoints: [path.join(root, 'web/App.tsx')],
  bundle: true,
  minify: true,
  format: 'esm',
  target: 'es2022',
  outfile: path.join(output, 'app.js'),
  legalComments: 'eof',
  define: { 'process.env.NODE_ENV': '"production"' },
});
const tailwind = spawnSync(
  process.execPath,
  [
    path.join(path.dirname(require.resolve('@tailwindcss/cli/package.json')), 'dist/index.mjs'),
    '-i',
    'web/app.src.css',
    '-o',
    path.join(output, 'app.css'),
    '--minify',
  ],
  { cwd: root, stdio: 'inherit' },
);
if (tailwind.status !== 0) process.exit(tailwind.status || 1);
const assets = [
  ['@fontsource-variable/inter/files/inter-latin-wght-normal.woff2', 'inter.woff2'],
  ['@fontsource-variable/space-grotesk/files/space-grotesk-latin-wght-normal.woff2', 'display.woff2'],
  ['@fontsource/ibm-plex-mono/files/ibm-plex-mono-latin-400-normal.woff2', 'mono.woff2'],
  ['@fontsource/ibm-plex-mono/files/ibm-plex-mono-latin-600-normal.woff2', 'mono-bold.woff2'],
  ['@fontsource-variable/inter/LICENSE', 'inter.LICENSE.txt'],
  ['@fontsource-variable/space-grotesk/LICENSE', 'display.LICENSE.txt'],
  ['@fontsource/ibm-plex-mono/LICENSE', 'mono.LICENSE.txt'],
  ['@rtkelly-labs/design-system/LICENSE', 'design-system.LICENSE.txt'],
  ['react-markdown/license', 'react-markdown.LICENSE.txt'],
  ['remark-gfm/license', 'remark-gfm.LICENSE.txt'],
];
for (const [source, name] of assets)
  await copyFile(path.join(root, 'node_modules', source), path.join(output, name));
if (checking) {
  const files = await readdir(output);
  const existing = await readdir(committed);
  if (files.toSorted().join() !== existing.toSorted().join())
    throw new Error('Web assets differ; run pnpm web:build');
  for (const file of files) {
    if (!(await readFile(path.join(output, file))).equals(await readFile(path.join(committed, file)))) {
      throw new Error(`${file} is stale; run pnpm web:build`);
    }
  }
  console.log('Committed web assets are current.');
}
