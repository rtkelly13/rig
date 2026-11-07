import type { Facts } from './facts';
import type { QueueView } from './router';
export interface Item {
  repo: string;
  number: number;
  kind: 'pr' | 'issue';
  title: string;
  state: string;
  created: string;
  updated: string;
  head: string;
  priority: number;
  readiness: number;
  lines: number;
  in_scope: boolean;
  body: string | null;
  facts?: Facts | null;
}
export interface Event {
  id: number;
  at: string;
  action: string;
  head: string;
  note: string;
  actor: string;
}
/** What the agent session on the PR's branch last said, from `estate wrapup` (docs/estate/wrapup.md). */
export interface AgentSignal {
  verdict: 'done' | 'pending' | 'asked';
  detail: string;
  at: string;
  head: string;
  worktree: string;
}
export interface Entry {
  item: Item;
  events: Event[];
  agent?: AgentSignal | null;
}
export interface QueueResponse {
  entries: Entry[];
  queue_path: string;
  error?: string;
}
export type Action =
  | 'opened'
  | 'unread'
  | 'archived'
  | 'unarchived'
  | 'review_requested'
  | 'reviewed'
  | 'changes_requested'
  | 'deferred'
  | 'resumed'
  | 'note';
export const readiness = ['Clean', 'Behind base', 'Blocked', 'Draft / unknown'];
export const ref = (item: Item) => `${item.repo}#${item.number}`;
export const isDeferred = (entry: Entry) =>
  entry.events.filter((event) => ['deferred', 'resumed'].includes(event.action)).at(-1)?.action ===
  'deferred';
export function hasNewDiscussion(entry: Entry, at: string): boolean {
  return entry.events.some(
    (event) =>
      event.action.startsWith('github:') &&
      !event.actor.includes('[bot]') &&
      Date.parse(event.at) > Date.parse(at),
  );
}
export function isArchived(entry: Entry): boolean {
  const event = entry.events.filter((event) => ['archived', 'unarchived'].includes(event.action)).at(-1);
  return Boolean(
    event?.action === 'archived' && event.head === entry.item.head && !hasNewDiscussion(entry, event.at),
  );
}
export const isActive = (entry: Entry) =>
  entry.item.in_scope && entry.item.state === 'OPEN' && !isDeferred(entry) && !isArchived(entry);
export function isWaiting(entry: Entry): boolean {
  const event = lastDecision(entry);
  return Boolean(
    isActive(entry) && event && event.head === entry.item.head && !hasNewDiscussion(entry, event.at),
  );
}
export function isUnread(entry: Entry): boolean {
  const event = entry.events.filter((event) => ['opened', 'unread'].includes(event.action)).at(-1);
  return (
    !event || event.action === 'unread' || event.head !== entry.item.head || hasNewDiscussion(entry, event.at)
  );
}
export function lastDecision(entry: Entry): Event | undefined {
  const reset = entry.events.filter((event) => ['resumed', 'unarchived'].includes(event.action)).at(-1);
  return entry.events
    .filter(
      (event) => ['reviewed', 'changes_requested'].includes(event.action) && event.id > (reset?.id || 0),
    )
    .at(-1);
}
export function reviewState(entry: Entry): string {
  if (isDeferred(entry)) return 'Snoozed';
  if (isArchived(entry)) return 'Archived';
  if (!entry.item.in_scope) return 'Out of scope';
  if (entry.item.state !== 'OPEN') return entry.item.state === 'MERGED' ? 'Merged' : 'Closed';
  const decision = lastDecision(entry);
  if (decision && decision.head !== entry.item.head) return 'Review new commit';
  if (decision && hasNewDiscussion(entry, decision.at)) return 'New discussion';
  if (decision?.action === 'changes_requested') return 'Waiting on changes';
  if (decision?.action === 'reviewed') return 'Reviewed this head';
  return entry.item.kind === 'pr' ? 'Needs your review' : 'Needs triage';
}
export function inView(entry: Entry, view: QueueView): boolean {
  if (view === 'snoozed') return entry.item.in_scope && entry.item.state === 'OPEN' && isDeferred(entry);
  if (view === 'done') return !entry.item.in_scope || entry.item.state !== 'OPEN' || isArchived(entry);
  if (view === 'history') return !isActive(entry);
  if (view === 'waiting') return isWaiting(entry);
  return isActive(entry) && !isWaiting(entry) && entry.item.kind === (view === 'issues' ? 'issue' : 'pr');
}
export function filterEntries(entries: Entry[], view: QueueView, query: URLSearchParams): Entry[] {
  const search = (query.get('q') || '').trim().toLowerCase();
  return entries.filter(
    (entry) =>
      inView(entry, view) &&
      (query.get('read') !== 'unread' || isUnread(entry)) &&
      (!search || `${ref(entry.item)} ${entry.item.title}`.toLowerCase().includes(search)) &&
      (!query.get('repo') || entry.item.repo === query.get('repo')) &&
      (!query.get('status') || String(entry.item.readiness) === query.get('status')),
  );
}
export function age(value: string): string {
  const days = Math.max(0, Math.floor((Date.now() - Date.parse(value)) / 86400000));
  return days === 0 ? 'today' : days === 1 ? '1 day' : `${days} days`;
}
export function when(value: string): string {
  return new Date(value).toLocaleString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}
export function githubUrl(item: Item, diff = false): string {
  return `https://github.com/${item.repo}/${item.kind === 'pr' ? 'pull' : 'issues'}/${item.number}${diff && item.kind === 'pr' ? '/files' : ''}`;
}
