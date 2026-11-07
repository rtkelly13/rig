/**
 * Reads the live Vercel account and writes two files:
 *
 *   out/sites.discovered.ts  — draft registry entries to merge into `sites.ts`
 *   out/import.json          — a Pulumi bulk-import file for `pulumi import -f`
 *
 * Run:  VERCEL_API_TOKEN=… pnpm discover
 *       VERCEL_API_TOKEN=… VERCEL_TEAM_ID=team_… pnpm discover
 *
 * This writes nothing to Vercel and touches no Pulumi state — every request is
 * a GET. It is safe to run repeatedly, and re-running after editing `sites.ts`
 * is the intended way to check for drift.
 *
 * Why a bulk-import file at all: a domain that already exists in Vercel cannot
 * simply be declared. `pulumi up` would try to CREATE it and Vercel rejects a
 * domain already attached to a project. Existing resources have to enter state
 * through `pulumi import` first; only then does the declaration match reality
 * and show an empty diff. That applies to `adopt` sites too — adoption skips
 * owning the *project*, not the domains.
 *
 * Resource names come from `naming.ts`, the same module the program uses, so
 * imported state lands on the resources the program declares. Do not hand-edit
 * the names in the generated file.
 */

import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { domainResourceName, projectResourceName } from './naming';
import { SITES } from './sites';

const API = 'https://api.vercel.com';

const token = process.env.VERCEL_API_TOKEN;
const teamId = process.env.VERCEL_TEAM_ID;

if (!token) {
  console.error(
    [
      'VERCEL_API_TOKEN is not set.',
      '',
      '`pnpm discover` resolves it from 1Password via .env.op, so an empty value',
      'usually means the reference did not resolve rather than a missing token —',
      'check `op whoami` and that the item path in .env.op exists. See ADOPTION.md.',
      '',
      'To bypass 1Password: VERCEL_API_TOKEN=… pnpm discover:env',
    ].join('\n'),
  );
  process.exit(1);
}

interface VercelProject {
  id: string;
  name: string;
  framework?: string | null;
  rootDirectory?: string | null;
  nodeVersion?: string | null;
  link?: {
    type?: string;
    org?: string;
    repo?: string;
    productionBranch?: string | null;
  } | null;
}

interface VercelDomain {
  name: string;
  gitBranch?: string | null;
  redirect?: string | null;
  redirectStatusCode?: number | null;
  verified?: boolean;
}

function withTeam(path: string): string {
  if (!teamId) return path;
  return path + (path.includes('?') ? '&' : '?') + `teamId=${encodeURIComponent(teamId)}`;
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(API + withTeam(path), {
    headers: { Authorization: `Bearer ${token}` },
  });
  if (!res.ok) {
    throw new Error(`GET ${path} -> ${res.status} ${res.statusText}: ${await res.text()}`);
  }
  return (await res.json()) as T;
}

/**
 * Vercel paginates with a `pagination.next` cursor that is a timestamp fed back
 * as `until`. Without following it an account with more than one page silently
 * reports a subset — which, for an adoption sweep, means quietly leaving
 * resources unmanaged.
 */
async function getAll<T>(path: string, key: string): Promise<T[]> {
  const out: T[] = [];
  let until: number | undefined;

  for (;;) {
    const sep = path.includes('?') ? '&' : '?';
    const page = await get<Record<string, unknown>>(
      `${path}${sep}limit=100${until ? `&until=${until}` : ''}`,
    );
    const items = (page[key] as T[]) ?? [];
    out.push(...items);

    const next = (page.pagination as { next?: number | null } | undefined)?.next;
    if (!next) return out;
    until = next;
  }
}

/** `sites.ts` is the source of truth for which key a project is filed under. */
function siteKeyFor(projectName: string): { key: string; known: boolean } {
  const existing = SITES.find((s) => s.projectName === projectName);
  if (existing) return { key: existing.key, known: true };
  return { key: projectName, known: false };
}

function tsString(value: string): string {
  return `'${value.replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
}

function renderSite(
  project: VercelProject,
  domains: VercelDomain[],
  key: string,
  known: boolean,
): string {
  const lines: string[] = [];
  const repo =
    project.link?.org && project.link?.repo ? `${project.link.org}/${project.link.repo}` : undefined;

  lines.push('  {');
  if (known) lines.push(`    // Already in sites.ts — shown for drift comparison.`);
  lines.push(`    key: ${tsString(key)},`);
  lines.push(`    projectName: ${tsString(project.name)},`);
  // Everything discovered already exists, so it is adopt-by-default. Switching a
  // site to 'manage' is a deliberate act that requires importing the project.
  lines.push(`    ownership: 'adopt',`);
  if (repo) lines.push(`    repo: ${tsString(repo)},`);
  if (project.link?.productionBranch) {
    lines.push(`    productionBranch: ${tsString(project.link.productionBranch)},`);
  }
  if (project.rootDirectory) lines.push(`    rootDirectory: ${tsString(project.rootDirectory)},`);
  if (project.nodeVersion) lines.push(`    nodeVersion: ${tsString(project.nodeVersion)},`);

  if (domains.length === 0) {
    lines.push('    domains: [],');
  } else {
    lines.push('    domains: [');
    for (const d of domains) {
      const parts = [`domain: ${tsString(d.name)}`];
      if (d.gitBranch) parts.push(`gitBranch: ${tsString(d.gitBranch)}`);
      if (d.redirect) parts.push(`redirect: ${tsString(d.redirect)}`);
      if (d.redirectStatusCode) parts.push(`redirectStatusCode: ${d.redirectStatusCode}`);
      const suffix = d.verified === false ? ' // NOT VERIFIED in Vercel' : '';
      lines.push(`      { ${parts.join(', ')} },${suffix}`);
    }
    lines.push('    ],');
  }
  lines.push('  },');
  return lines.join('\n');
}

async function main(): Promise<void> {
  console.log(`Reading Vercel account${teamId ? ` (team ${teamId})` : ''}…`);

  const projects = await getAll<VercelProject>('/v9/projects', 'projects');
  console.log(`Found ${projects.length} project(s).`);

  const imports: { type: string; name: string; id: string }[] = [];
  const rendered: string[] = [];
  const skippedDomains: string[] = [];
  let domainCount = 0;
  let unknownCount = 0;

  // Sorted so re-running produces a stable diff rather than API ordering churn.
  projects.sort((a, b) => a.name.localeCompare(b.name));

  for (const project of projects) {
    const domains = await getAll<VercelDomain>(`/v9/projects/${project.id}/domains`, 'domains');
    domains.sort((a, b) => a.name.localeCompare(b.name));
    domainCount += domains.length;

    const { key, known } = siteKeyFor(project.name);
    if (!known) unknownCount++;

    rendered.push(renderSite(project, domains, key, known));

    // Only emit imports for what the program actually declares. Importing a
    // resource the program does not declare puts an orphan in state that the
    // next `pulumi up` proposes to DELETE — for a domain, that unhosts a live
    // site. So: nothing for sites absent from sites.ts (they are still drafts),
    // no Project for `adopt` sites (those are looked up, never declared), and
    // no domain that sites.ts has not opted into.
    const declared = SITES.find((s) => s.projectName === project.name);
    if (!declared) continue;

    if (declared.ownership === 'manage') {
      imports.push({
        type: 'vercel:index/project:Project',
        name: projectResourceName(declared.key),
        id: teamId ? `${teamId}/${project.id}` : project.id,
      });
    }

    for (const d of domains) {
      if (!declared.domains.some((decl) => decl.domain === d.name)) {
        skippedDomains.push(`${project.name}: ${d.name} (live, not in sites.ts)`);
        continue;
      }
      imports.push({
        type: 'vercel:index/projectDomain:ProjectDomain',
        name: domainResourceName(declared.key, d.name),
        id: teamId ? `${teamId}/${project.id}/${d.name}` : `${project.id}/${d.name}`,
      });
    }
  }

  // cwd, not __dirname: this runs compiled from `bin/`, so __dirname would put
  // the output inside the throwaway build directory. pnpm runs scripts from the
  // package root, which is where `out/` belongs.
  const outDir = join(process.cwd(), 'out');
  mkdirSync(outDir, { recursive: true });

  const header = [
    '// GENERATED by `pnpm discover` — do not edit in place.',
    '//',
    '// Merge the entries you want into `sites.ts`, then import their existing',
    '// resources with `pulumi import -f out/import.json --generate-code=false`',
    '// before running `pulumi up`. See ADOPTION.md.',
    '',
    'import type { SiteSpec } from \'./sites\';',
    '',
    'export const DISCOVERED: SiteSpec[] = [',
  ].join('\n');

  writeFileSync(join(outDir, 'sites.discovered.ts'), `${header}\n${rendered.join('\n')}\n];\n`);
  writeFileSync(join(outDir, 'import.json'), `${JSON.stringify({ resources: imports }, null, 2)}\n`);

  console.log(`\nWrote infra/vercel/out/sites.discovered.ts (${projects.length} sites, ${domainCount} domains)`);
  console.log(`Wrote infra/vercel/out/import.json (${imports.length} resources to import)`);
  if (unknownCount > 0) {
    console.log(
      `\n${unknownCount} project(s) are not yet in sites.ts — nothing is imported for them until you merge them in and re-run.`,
    );
  }
  if (skippedDomains.length > 0) {
    console.log(`\n${skippedDomains.length} live domain(s) not declared in sites.ts (left untouched):`);
    for (const s of skippedDomains) console.log(`  - ${s}`);
  }
  console.log('\nNext: read ADOPTION.md. Nothing has been changed in Vercel or Pulumi.');
}

main().catch((err: unknown) => {
  console.error(err instanceof Error ? err.message : err);
  process.exit(1);
});
