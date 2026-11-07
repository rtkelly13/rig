import * as pulumi from '@pulumi/pulumi';
import * as vercel from '@pulumiverse/vercel';
import { DNS_RECORDS } from './dns';
import { dnsResourceName, domainResourceName, envResourceName, projectResourceName } from './naming';
import { type SiteSpec, SITES } from './sites';

/**
 * Pulumi program managing Vercel projects and the domains pointed at them.
 *
 * This is the engine; [`sites.ts`](./sites.ts) is the data. Adding a site should
 * be an edit to the registry, not to this file.
 *
 * Scope is deliberately identity + domains + env vars. Build configuration lives
 * in each repo's `vercel.json`, which Vercel reads at build time and which
 * overrides project settings — declaring it in both places would leave two
 * sources of truth with a silent winner.
 *
 * Auth: `VERCEL_API_TOKEN`, supplied by `pnpm pulumi` (which wraps the CLI in
 * `op run`). Run it that way rather than bare `pulumi`; see ADOPTION.md.
 */
const config = new pulumi.Config();

/** Vercel team. Personal accounts leave this unset. */
const teamId = config.get('teamId');

/**
 * Restrict a run to one site: `pulumi config set only design-system-storybook`.
 * Useful when adding a site to a stack that already manages live domains — you
 * can preview one entry without reading a diff covering everything.
 */
const only = config.get('only');

const selected = only ? SITES.filter((s) => s.key === only) : SITES;

if (only && selected.length === 0) {
  throw new Error(
    `config 'only' is "${only}", which matches no site key. Known keys: ${SITES.map((s) => s.key).join(', ')}`,
  );
}

function projectIdFor(site: SiteSpec): pulumi.Input<string> {
  if (site.ownership === 'adopt') {
    // Looked up, never declared: an adopted project is one that is already
    // serving traffic, and a `pulumi destroy` here must not be able to take it.
    return vercel.getProjectOutput({ name: site.projectName, teamId }).id;
  }

  if (!site.repo) {
    throw new Error(`site "${site.key}" is managed but has no repo; Vercel needs one to build from`);
  }

  const project = new vercel.Project(projectResourceName(site.key), {
    name: site.projectName,
    teamId,
    framework: site.framework,
    nodeVersion: site.nodeVersion,
    serverlessFunctionRegion: site.serverlessFunctionRegion,
    rootDirectory: site.rootDirectory,
    ignoreCommand: site.ignoreCommand,
    gitRepository: {
      type: 'github',
      repo: site.repo,
      productionBranch: site.productionBranch ?? 'main',
    },
  });

  return project.id;
}

const summary: Record<string, { projectName: string; ownership: string; domains: string[] }> = {};

for (const site of selected) {
  const projectId = projectIdFor(site);

  for (const spec of site.domains) {
    new vercel.ProjectDomain(domainResourceName(site.key, spec.domain), {
      projectId,
      teamId,
      domain: spec.domain,
      // Presence of `gitBranch` is what makes this a branch domain rather than
      // a second production alias. Without it, both domains serve production.
      gitBranch: spec.gitBranch,
      redirect: spec.redirect,
      redirectStatusCode: spec.redirectStatusCode,
    });
  }

  for (const env of site.environmentVariables ?? []) {
    new vercel.ProjectEnvironmentVariable(envResourceName(site.key, env.key), {
      projectId,
      teamId,
      key: env.key,
      value: env.value,
      targets: env.targets ?? ['production', 'preview', 'development'],
    });
  }

  summary[site.key] = {
    projectName: site.projectName,
    ownership: site.ownership,
    domains: site.domains.map((d) => (d.gitBranch ? `${d.domain} (branch: ${d.gitBranch})` : d.domain)),
  };
}

const dnsSummary: Record<string, string> = {};

if (!only) {
  for (const record of DNS_RECORDS) {
    new vercel.DnsRecord(dnsResourceName(record.domain, record.name, record.type), {
      domain: record.domain,
      teamId,
      name: record.name,
      type: record.type,
      value: record.value,
      ttl: record.ttl ?? 60,
      comment: record.comment,
    });
    dnsSummary[`${record.name}.${record.domain}`] = `${record.type} -> ${record.value}`;
  }
}

export const sites = summary;
export const dns = dnsSummary;
export const managedSiteCount = selected.length;

