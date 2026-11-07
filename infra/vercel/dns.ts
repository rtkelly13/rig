/**
 * Non-site DNS records for the ryankelly.dev zone.
 *
 * Site domains (Vercel projects) are managed in `sites.ts`.
 * This registry is for external DNS records pointed elsewhere (GitHub Pages, email, verification).
 */

export interface DnsRecordSpec {
  /** The parent domain/zone, e.g. 'ryankelly.dev'. */
  domain: string;
  /** Subdomain prefix, e.g. 'docs' for 'docs.ryankelly.dev'. */
  name: string;
  /** Record type: 'CNAME', 'A', 'TXT', etc. */
  type: 'CNAME' | 'A' | 'AAAA' | 'TXT' | 'MX';
  /** Target value. FQDNs for CNAME must end with a dot, e.g. 'rtkelly13.github.io.'. */
  value: string;
  /** Time to live in seconds. Defaults to 60. */
  ttl?: number;
  /** Explanatory comment in Vercel DNS. */
  comment?: string;
}

export const DNS_RECORDS: DnsRecordSpec[] = [
  {
    domain: 'ryankelly.dev',
    name: 'docs',
    type: 'CNAME',
    value: 'rtkelly13.github.io.',
    ttl: 60,
    comment: 'GitHub Pages CNAME for docs.ryankelly.dev (Parquet Documentation Portal)',
  },
];
