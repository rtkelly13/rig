/**
 * Pulumi resource names, in one place.
 *
 * These are shared by the program (`index.ts`, which declares resources) and the
 * discovery script (`discover.ts`, which writes the bulk-import file). They MUST
 * agree: `pulumi import` matches an existing cloud resource to a program
 * resource by name, and a mismatch silently produces a duplicate — Pulumi
 * imports the resource under the name in the file, then `pulumi up` tries to
 * create the one the program declares, and the domain create fails because the
 * domain is already attached.
 *
 * Deriving both from these functions is what stops that happening. Do not
 * hand-write a resource name anywhere else.
 */

/** Domains become resource names, and `.` is not meaningful in a Pulumi URN. */
export function resourceSuffix(value: string): string {
  return value.replace(/\./g, '-');
}

/** The `vercel.Project` resource name for a site. */
export function projectResourceName(siteKey: string): string {
  return siteKey;
}

/** The `vercel.ProjectDomain` resource name for one domain of a site. */
export function domainResourceName(siteKey: string, domain: string): string {
  return `${siteKey}-${resourceSuffix(domain)}`;
}

/** The `vercel.ProjectEnvironmentVariable` resource name for one env var. */
export function envResourceName(siteKey: string, key: string): string {
  return `${siteKey}-env-${key.toLowerCase()}`;
}

/** The `vercel.DnsRecord` resource name for an external DNS record. */
export function dnsResourceName(domain: string, name: string, type: string): string {
  return `dns-${resourceSuffix(domain)}-${name}-${type.toLowerCase()}`;
}

