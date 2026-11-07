import { useMemo, useSyncExternalStore } from 'react';

export type QueueView = 'inbox' | 'pulls' | 'issues' | 'waiting' | 'snoozed' | 'done' | 'history';
export type OpsPage =
  'ops' | 'ops-jobs' | 'ops-runs' | 'ops-telemetry' | 'ops-decisions' | 'ops-trim';
export type Page =
  | QueueView
  | OpsPage
  | 'references'
  | 'estate'
  | 'operations'
  | 'studio'
  | 'optical'
  | 'missing';
const OPS_PAGES: Record<string, OpsPage> = {
  jobs: 'ops-jobs',
  runs: 'ops-runs',
  telemetry: 'ops-telemetry',
  decisions: 'ops-decisions',
  trim: 'ops-trim',
};
export const OPS_TITLES: Record<OpsPage, string> = {
  ops: 'Attention',
  'ops-jobs': 'Jobs',
  'ops-runs': 'Runs',
  'ops-telemetry': 'Telemetry',
  'ops-decisions': 'Decisions',
  'ops-trim': 'Trim',
};
export function isOpsPage(page: Page): page is OpsPage {
  return page in OPS_TITLES;
}
export interface Route {
  page: Page;
  repository: string;
  selected: string;
  query: URLSearchParams;
}

export function parseRoute(value: string): Route {
  const url = new URL(value, 'http://rig.local');
  let parts: string[];
  try {
    parts = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
  } catch {
    return { repository: '', page: 'missing', selected: '', query: url.searchParams };
  }
  if (!parts.length || url.pathname === '/index.html')
    return { repository: '', page: 'inbox', selected: '', query: url.searchParams };
  if (parts[0] === 'ops' && parts.length <= 2) {
    const page = parts.length === 1 ? 'ops' : OPS_PAGES[parts[1] ?? ''];
    return { page: page ?? 'missing', repository: '', selected: '', query: url.searchParams };
  }
  if (parts.length === 2 && parts[0] === 'estate' && parts[1] === 'operations')
    return { page: 'operations', repository: '', selected: '', query: url.searchParams };
  if (['estate', 'studio', 'optical'].includes(parts[0] ?? '') && parts.length === 1) {
    return { repository: '', page: parts[0] as Page, selected: '', query: url.searchParams };
  }
  if (parts[0] === 'repos' && parts[3] === 'triage' && (parts.length === 5 || parts.length === 6)) {
    const owner = parts[1] || '',
      name = parts[2] || '',
      view = parts[4] || '';
    const repository = `${owner}/${name}`.toLowerCase();
    if (
      /^[a-zA-Z0-9-]+$/.test(owner) &&
      /^[a-zA-Z0-9_.-]+$/.test(name) &&
      !['.', '..'].includes(name) &&
      ['inbox', 'pulls', 'issues', 'waiting', 'snoozed', 'done', 'history'].includes(view) &&
      (parts.length === 5 || /^[1-9][0-9]*$/.test(parts[5] || ''))
    )
      return {
        page: view as Page,
        repository,
        selected: parts[5] ? `${repository}#${parts[5]}` : '',
        query: url.searchParams,
      };
    return { page: 'missing', repository: '', selected: '', query: url.searchParams };
  }
  const page = parts[1] || 'inbox';
  if (
    parts[0] !== 'triage' ||
    !['inbox', 'pulls', 'issues', 'waiting', 'snoozed', 'done', 'history', 'references'].includes(
      page,
    ) ||
    (parts.length !== 2 && parts.length !== 1 && parts.length !== 5) ||
    (page === 'references' && parts.length > 2)
  ) {
    return { repository: '', page: 'missing', selected: '', query: url.searchParams };
  }
  const selected = parts.length === 5 ? `${parts[2]}/${parts[3]}#${parts[4]}` : '';
  return { repository: '', page: page as Page, selected, query: url.searchParams };
}

export function itemUrl(
  page: QueueView,
  repo: string,
  number: number,
  query = new URLSearchParams(),
  repository = '',
): string {
  const suffix = query.size ? `?${query}` : '';
  return repository === repo
    ? `/repos/${repository}/triage/${page}/${number}${suffix}`
    : `/triage/${page}/${repo}/${number}${suffix}`;
}

export function queueUrl(page: QueueView, repository = '', query = new URLSearchParams()): string {
  const path = repository ? `/repos/${repository}/triage/${page}` : `/triage/${page}`;
  return `${path}${query.size ? `?${query}` : ''}`;
}

export function navigate(value: string, replace = false): void {
  if (`${location.pathname}${location.search}` === value) return;
  history[replace ? 'replaceState' : 'pushState'](null, '', value);
  window.dispatchEvent(new PopStateEvent('popstate'));
}

const subscribe = (listener: () => void) => {
  window.addEventListener('popstate', listener);
  return () => window.removeEventListener('popstate', listener);
};
const snapshot = () => `${location.pathname}${location.search}`;
export function useRoute(): Route {
  const value = useSyncExternalStore(subscribe, snapshot);
  return useMemo(() => parseRoute(value), [value]);
}

export function updateQuery(name: string, value: string, replace = false): void {
  const query = new URLSearchParams(location.search);
  value ? query.set(name, value) : query.delete(name);
  navigate(`${location.pathname}${query.size ? `?${query}` : ''}`, replace);
}
