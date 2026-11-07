/**
 * The site registry — the data half of this stack.
 *
 * Adding a site should be an edit to this file and nothing else; `index.ts` is
 * a loop over it. Keep the declarations here free of Pulumi resources so the
 * shape of "what is hosted" stays readable without knowing the provider API.
 */

export interface DomainSpec {
  /** Fully-qualified domain, e.g. `design-system.ryankelly.dev`. */
  domain: string;
  /**
   * Set to make this a **branch domain** — Vercel points it at the newest
   * deployment of that branch. Omit for the production domain, which follows
   * the project's production branch.
   *
   * A branch domain maps to exactly one branch. There is no way to make a
   * single custom domain follow "whichever PR deployed last"; per-PR
   * deployments keep their generated `*.vercel.app` URLs.
   */
  gitBranch?: string;
  /** Redirect this domain elsewhere instead of serving it. */
  redirect?: string;
  redirectStatusCode?: number;
}

export interface EnvVarSpec {
  key: string;
  value: string;
  /** Defaults to all three targets. */
  targets?: string[];
}

export interface SiteSpec {
  /**
   * Pulumi resource name. Stable identity — renaming this replaces every
   * resource for the site, which for a domain means a window where it does not
   * resolve. Choose once.
   */
  key: string;

  /** Vercel project name. */
  projectName: string;

  /**
   * How this stack relates to the Vercel project:
   *
   * - `manage` — Pulumi creates and owns it. Only for projects that do not
   *   exist yet; declaring one that already exists will fail on create.
   * - `adopt` — the project already exists and stays owned by whoever made it.
   *   Pulumi looks it up by name and attaches only domains and env vars, so a
   *   `pulumi destroy` can never take the project (or the site) with it.
   *
   * Prefer `adopt` for anything already serving traffic.
   */
  ownership: 'manage' | 'adopt';

  /** `owner/repo`. Required for `manage`, ignored for `adopt`. */
  repo?: string;
  productionBranch?: string;

  /**
   * Subdirectory Vercel treats as the project root. Its `vercel.json` is the
   * one that applies — which is how one repo can back two projects with
   * different builds.
   */
  rootDirectory?: string;

  /**
   * Command to determine whether to skip a build (Ignored Build Step).
   * Exit code 1 proceeds with the build; exit code 0 skips/cancels it.
   * Runs after the deployment is created, so it skips a build but saves no quota.
   * Gate creation with `git.deploymentEnabled` in the repo's `vercel.json` instead.
   */
  ignoreCommand?: string;

  nodeVersion?: string;
  framework?: string;
  serverlessFunctionRegion?: string;
  domains: DomainSpec[];
  environmentVariables?: EnvVarSpec[];

  /** Free-text note surfaced in `pulumi stack output sites`. */
  note?: string;
}

/**
 * Vercel projects and the domains pointed at them.
 *
 * Build settings deliberately are NOT declared here. Vercel reads `vercel.json`
 * from each repo at build time and it overrides project settings, so putting
 * `buildCommand`/`outputDirectory` in both places leaves two sources of truth
 * with a silent winner. This stack owns identity, domains and env; the repos
 * own how they build.
 *
 * Adding an `ownership: 'manage'` entry for a project that does not exist yet
 * makes the next `pulumi up` CREATE it, which is a louder thing than the adopts
 * that make up most of this file. Preview before applying and check the create
 * count is what you expect; if a `manage` entry is not meant to be applied yet,
 * keep it out of this array until it is.
 */
export const SITES: SiteSpec[] = [
  {
    key: 'maze',
    projectName: 'maze',
    ownership: 'adopt',
    // No git repository is linked in Vercel. ProceduralGeneration3DMazes pushes
    // builds with `vercel deploy build/web` from .github/scripts/deploy-vercel.ps1,
    // so there is no branch to build from and this can only ever be `adopt`.
    domains: [
      // Production. The deploy script targets it with `--prod`, deliberately
      // stated rather than inferred from the ref.
      { domain: 'maze.ryankelly.dev' },
      // Preview slots p1-p3 (aliased on slot/1..3 workflow dispatch or push)
      { domain: 'p1.maze.ryankelly.dev' },
      { domain: 'p2.maze.ryankelly.dev' },
      { domain: 'p3.maze.ryankelly.dev' },
    ],
    note: 'Godot WebGL maze build. Deployed by workflow dispatch and slot pushes.',
  },
  {
    key: 'blog',
    projectName: 'blog',
    ownership: 'manage',
    repo: 'rtkelly13/blog',
    productionBranch: 'production',
    nodeVersion: '24.x',
    framework: 'nextjs',
    serverlessFunctionRegion: 'iad1',
    domains: [
      { domain: 'ryankelly.dev' },
      { domain: 'blog.ryankelly.dev' },
      { domain: 'www.ryankelly.dev', redirect: 'ryankelly.dev', redirectStatusCode: 308 },
      // Scoped preview slots. The schema-migration train rotates work through slot/1..3.
      { domain: 'p1.blog.ryankelly.dev', gitBranch: 'slot/1' },
      { domain: 'p2.blog.ryankelly.dev', gitBranch: 'slot/2' },
      { domain: 'p3.blog.ryankelly.dev', gitBranch: 'slot/3' },
    ],
    note: 'The blog at ryankelly.dev and blog.ryankelly.dev, plus p1-p3.blog preview slots.',
  },
  {
    key: 'blog-storybook',
    projectName: 'blog-storybook',
    // The one `manage` site: this project does not exist yet, so Pulumi creates
    // it. Everything else here was adopted.
    ownership: 'manage',
    repo: 'rtkelly13/blog',
    productionBranch: 'production',
    nodeVersion: '22.x',
    // Reads `storybook-site/vercel.json` so the blog's own root `vercel.json`
    // (which deploys the Next.js site) does not apply here.
    //
    // MANUAL STEP: this project also needs "Include source files outside of the
    // Root Directory in the Build Step" ticked in the dashboard — the Storybook
    // build needs the whole repo. The Vercel provider does not expose that flag.
    rootDirectory: 'storybook-site',
    // A domain of our own rather than the generated *.vercel.app URL, which
    // Vercel named `blog-storybook-theta` because the bare project name was
    // taken and would name differently if this project were ever recreated.
    // This is the URL the design system composes, so it is worth it being one
    // we control and can read.
    //
    // ryankelly.dev is on Vercel's nameservers, so declaring the domain here is
    // all that is needed — Vercel creates the DNS record itself.
    domains: [
      { domain: 'blog-storybook.ryankelly.dev' },
      // Scoped Storybook preview slots matching blog preview slots
      { domain: 'p1.blog-storybook.ryankelly.dev', gitBranch: 'slot/1' },
      { domain: 'p2.blog-storybook.ryankelly.dev', gitBranch: 'slot/2' },
      { domain: 'p3.blog-storybook.ryankelly.dev', gitBranch: 'slot/3' },
    ],
    note: 'Blog Storybook at blog-storybook.ryankelly.dev, plus p1-p3.blog-storybook preview slots.',
  },
  {
    key: 'design-system-storybook',
    projectName: 'design-system-storybook',
    ownership: 'manage',
    repo: 'rtkelly-labs/design-system',
    productionBranch: 'production',
    nodeVersion: '22.x',
    domains: [
      { domain: 'design-system.ryankelly.dev' },
      // Scoped Design System preview slots
      { domain: 'p1.design-system.ryankelly.dev', gitBranch: 'slot/1' },
      { domain: 'p2.design-system.ryankelly.dev', gitBranch: 'slot/2' },
      { domain: 'p3.design-system.ryankelly.dev', gitBranch: 'slot/3' },
    ],
    environmentVariables: [
      {
        // Composes the blog's Storybook into the sidebar. Baked into the
        // manager bundle at build time, so changing it needs a redeploy.
        //
        // Points at blog-storybook's own domain, declared above, rather than a
        // generated *.vercel.app name. Vercel named that one
        // `blog-storybook-theta` because the bare project name was taken, and
        // it could name a recreated project differently.
        //
        // Never a per-deployment URL: deployment protection 302s those to
        // Vercel SSO, so the manager's cross-origin fetch of /index.json would
        // get a redirect to a login page. The production alias and custom
        // domains serve publicly; deployment URLs do not.
        //
        // Getting this wrong fails quietly — the ref renders as a
        // permanently-erroring sidebar entry while both Storybooks are healthy
        // served on their own.
        key: 'STORYBOOK_REF_BLOG_URL',
        value: 'https://blog-storybook.ryankelly.dev',
      },
    ],
    note: 'Design system Storybook, with the blog Storybook composed in.',
  },
];
