// Builds every committed web asset: the SPA bundle and fonts, and each Tailwind
// entry. The outputs are committed because the Python CLI and server serve them
// without Node. `--check` builds into temp/ and fails if any committed file
// differs, so a source edit or a design-system bump that changes output is caught.
import { build } from 'esbuild';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { readFile, mkdir, copyFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const require = createRequire(import.meta.url);
const checking = process.argv.includes('--check');
const scratch = path.join(root, 'temp/web-build-check');
const outputFor = (committed) => (checking ? path.join(scratch, committed) : path.join(root, committed));

// Every Tailwind entry and the committed file it compiles to. Each entry imports
// its tokens from @rtkelly-labs/design-system.
const stylesheets = [
  ['web/app.src.css', 'src/rig/image_drift/templates/app_assets/app.css'],
  ['styles/gallery.src.css', 'src/rig/image_drift/templates/gallery.css'],
  ['styles/c4.src.css', 'src/rig/c4_okf/static/c4.css'],
];

const appAssets = 'src/rig/image_drift/templates/app_assets';
await mkdir(outputFor(appAssets), { recursive: true });
await build({
  entryPoints: [path.join(root, 'web/App.tsx')],
  bundle: true,
  minify: true,
  format: 'esm',
  target: 'es2022',
  outfile: path.join(outputFor(appAssets), 'app.js'),
  legalComments: 'eof',
  define: { 'process.env.NODE_ENV': '"production"' },
});
const tailwindCli = path.join(path.dirname(require.resolve('@tailwindcss/cli/package.json')), 'dist/index.mjs');
for (const [source, committed] of stylesheets) {
  await mkdir(path.dirname(outputFor(committed)), { recursive: true });
  const tailwind = spawnSync(process.execPath, [tailwindCli, '-i', source, '-o', outputFor(committed), '--minify'], {
    cwd: root,
    stdio: 'inherit',
  });
  if (tailwind.status !== 0) process.exit(tailwind.status || 1);
}
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
  await copyFile(path.join(root, 'node_modules', source), path.join(outputFor(appAssets), name));
if (checking) {
  const files = await readdir(outputFor(appAssets));
  const existing = await readdir(path.join(root, appAssets));
  if (files.toSorted().join() !== existing.toSorted().join())
    throw new Error('Web assets differ; run pnpm css:build');
  const committed = [
    ...files.map((file) => path.join(appAssets, file)),
    ...stylesheets.map(([, output]) => output).filter((output) => !output.startsWith(appAssets)),
  ];
  for (const file of committed) {
    if (!(await readFile(outputFor(file))).equals(await readFile(path.join(root, file)))) {
      throw new Error(`${file} is stale; run pnpm css:build`);
    }
  }
  console.log('Committed web assets are current.');
}
