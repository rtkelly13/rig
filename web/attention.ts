import { hasNewDiscussion, lastDecision, ref } from './model';
import type { Entry } from './model';
import type { IconName } from './Icon';

export type Tone = 'danger' | 'warning' | 'info' | 'success' | 'quiet';
export interface Signal {
  key: string;
  label: string;
  tone: Tone;
  icon: IconName;
  action: boolean;
  title?: string;
}
export const failedStates = new Set([
  'FAILURE',
  'ERROR',
  'TIMED_OUT',
  'ACTION_REQUIRED',
  'STARTUP_FAILURE',
  'CANCELLED',
  'STALE',
]);
export const pendingStates = new Set([
  'PENDING',
  'QUEUED',
  'IN_PROGRESS',
  'WAITING',
  'REQUESTED',
  'EXPECTED',
]);
export const actionNames: Record<string, string> = {
  'checks-failed': 'Failed checks',
  conflict: 'Merge conflicts',
  changes: 'Changes requested',
  threads: 'Unresolved threads',
  reply: 'New discussion',
  review: 'Review needed',
  unassigned: 'Unassigned issues',
  priority: 'Needs priority',
  draft: 'Drafts',
  dependency: 'Stack dependencies',
  unknown: 'Signals not fetched',
  'agent-done': 'Agent says done',
  'agent-pending': 'Agent left you a task',
  'agent-asked': 'Agent asked, no reply',
};
const clip = (text: string, size: number) =>
  text.length > size ? `${text.slice(0, size - 1).trimEnd()}…` : text;
/** The wrap-up verdict of the agent on this PR's branch, if `estate wrapup` asked it. */
export function agentSignal(entry: Entry): Signal | null {
  const agent = entry.agent;
  if (!agent || entry.item.state !== 'OPEN') return null;
  const when = new Date(agent.at).toLocaleString();
  const stale = Boolean(agent.head && agent.head !== entry.item.head);
  const where = `${agent.worktree} · ${when}${stale ? ' · answered at a different commit from the PR head' : ''}`;
  if (agent.verdict === 'done')
    return {
      key: 'agent-done',
      label: stale ? 'Agent: done, older commit' : 'Agent: done',
      tone: stale ? 'quiet' : 'success',
      icon: 'check',
      action: !stale,
      title: where,
    };
  if (agent.verdict === 'pending')
    return {
      key: 'agent-pending',
      label: `Agent: ${clip(agent.detail || 'pending', 40)}`,
      tone: 'warning',
      icon: 'user',
      action: true,
      title: `${agent.detail}\n${where}`,
    };
  return {
    key: 'agent-asked',
    label: 'Agent asked, no reply',
    tone: 'quiet',
    icon: 'clock',
    action: false,
    title: where,
  };
}
/** The first *count* signals, keeping the agent's verdict in view when there is one. */
export function visibleSignals(entry: Entry, count = 3): Signal[] {
  const all = signals(entry);
  const top = all.slice(0, count);
  const agent = all.find((signal) => signal.key.startsWith('agent-'));
  return agent && !top.includes(agent) ? [...all.slice(0, count - 1), agent] : top;
}
export function signals(entry: Entry): Signal[] {
  const { item } = entry;
  if (item.state !== 'OPEN')
    return [
      {
        key: 'closed',
        label: item.state === 'MERGED' ? 'Merged' : 'Closed',
        tone: 'quiet',
        icon: 'check',
        action: false,
      },
    ];
  const result: Signal[] = [];
  const facts = item.facts;
  const add = (key: string, label: string, tone: Tone, icon: IconName, action = true) =>
    result.push({ key, label, tone, icon, action });
  const decision = lastDecision(entry);
  const issueNote =
    item.kind === 'issue'
      ? entry.events.filter((event) => event.action === 'note').at(-1)
      : undefined;
  if (decision && decision.head !== item.head)
    add('review', 'New commit to review', 'info', 'pull');
  if (decision && hasNewDiscussion(entry, decision.at))
    add('reply', 'Read new replies', 'info', 'message');
  if (issueNote && hasNewDiscussion(entry, issueNote.at))
    add('reply', 'Read new replies', 'info', 'message');
  const agent = agentSignal(entry);
  if (agent) result.push(agent);
  if (!facts) {
    add('unknown', 'Signals not fetched', 'quiet', 'refresh');
    return result;
  }
  if (failedStates.has(facts.checks_state)) {
    const failed = facts.checks.filter((check) => failedStates.has(check.state)).length;
    add(
      'checks-failed',
      failed
        ? `${failed}${facts.partial.includes('Checks') ? '+' : ''} failed ${failed === 1 ? 'check' : 'checks'}`
        : 'Checks failed',
      'danger',
      'alert',
    );
  }
  if (facts.mergeable === 'CONFLICTING') add('conflict', 'Resolve conflicts', 'danger', 'merge');
  if (facts.review_decision === 'CHANGES_REQUESTED')
    add('changes', 'Address review changes', 'warning', 'review');
  if (facts.unresolved_threads)
    add(
      'threads',
      `${facts.unresolved_threads}${facts.partial.includes('Review threads') ? '+' : ''} unresolved ${facts.unresolved_threads === 1 ? 'thread' : 'threads'}`,
      'warning',
      'message',
    );
  if (facts.review_requested) add('review', 'Your review requested', 'info', 'review');
  else if (item.kind === 'pr' && !facts.draft && facts.review_decision === 'REVIEW_REQUIRED')
    add(
      'review',
      facts.viewer_authored
        ? facts.reviewers.length
          ? 'Awaiting review'
          : 'Request review'
        : 'Review needed',
      facts.viewer_authored ? 'quiet' : 'info',
      'review',
      !facts.viewer_authored || !facts.reviewers.length,
    );
  if (facts.draft)
    add(
      'draft',
      facts.viewer_authored ? 'Draft · mark ready' : 'Draft',
      'quiet',
      'pull',
      facts.viewer_authored,
    );
  if (item.kind === 'issue' && !facts.assignees.length && !facts.partial.includes('Assignees'))
    add('unassigned', 'Assign owner', 'warning', 'user');
  if (item.kind === 'issue' && item.priority === 4)
    add('priority', 'Set priority', 'warning', 'tag');
  const stack = facts.stack;
  if (stack && stack.position > 1) {
    const parent = stack.members.find((member) => member.position === stack.position - 1);
    if (parent?.state === 'OPEN')
      add('dependency', `After #${parent.number}`, 'quiet', 'stack', false);
  }
  if (
    item.kind === 'pr' &&
    !facts.draft &&
    !facts.viewer_authored &&
    !decision &&
    !result.some((signal) => signal.key === 'review')
  )
    add('review', 'Review this PR', 'info', 'review');
  if (pendingStates.has(facts.checks_state))
    add('checks-running', 'Checks running', 'info', 'clock', false);
  else if (facts.checks_state === 'SUCCESS')
    add('checks-passed', 'Checks passed', 'success', 'check', false);
  else if (item.kind === 'pr' && facts.checks_state === 'UNKNOWN')
    add('checks-unknown', 'Checks unknown', 'quiet', 'clock', false);
  if (facts.review_decision === 'APPROVED') add('approved', 'Approved', 'success', 'review', false);
  if (facts.comments) add('comments', `${facts.comments} comments`, 'info', 'message', false);
  if (
    item.kind === 'issue' &&
    facts.related.some((related) => related.kind === 'pr' && related.state === 'OPEN')
  )
    add('linked', 'Linked PR in progress', 'info', 'link', false);
  const severity: Record<Tone, number> = { danger: 0, warning: 1, info: 2, quiet: 3, success: 4 };
  return result.sort(
    (a, b) => Number(b.action) - Number(a.action) || severity[a.tone] - severity[b.tone],
  );
}
export interface QueueGroup {
  key: string;
  title: string;
  description: string;
  icon: IconName;
  entries: Entry[];
  grouped: boolean;
}
interface BranchChain {
  key: string;
  depth: number;
}
function inferredChains(estate: Entry[]): Map<string, BranchChain> {
  const candidates = estate.filter(
    ({ item }) =>
      item.kind === 'pr' && item.facts?.head && !item.facts.stack && !item.facts.cross_repository,
  );
  const byHead = new Map<string, Entry[]>();
  const byRef = new Map(candidates.map((entry) => [ref(entry.item), entry]));
  const edges = new Map<string, Set<string>>();
  const parents = new Map<string, string>();
  for (const entry of candidates) {
    const key = `${entry.item.repo}:${entry.item.facts?.head}`;
    const values = byHead.get(key) || [];
    values.push(entry);
    byHead.set(key, values);
    edges.set(ref(entry.item), new Set());
  }
  for (const entry of candidates) {
    const matches = byHead.get(`${entry.item.repo}:${entry.item.facts?.base}`);
    if (matches?.length !== 1 || matches[0] === entry) continue;
    const parent = matches[0];
    if (!parent) continue;
    const childKey = ref(entry.item),
      parentKey = ref(parent.item);
    edges.get(childKey)?.add(parentKey);
    edges.get(parentKey)?.add(childKey);
    parents.set(childKey, parentKey);
  }
  const result = new Map<string, BranchChain>(),
    visited = new Set<string>();
  for (const entry of candidates) {
    const start = ref(entry.item);
    if (visited.has(start)) continue;
    const pending = [start],
      component: string[] = [];
    while (pending.length) {
      const key = pending.pop();
      if (!key || visited.has(key)) continue;
      visited.add(key);
      component.push(key);
      for (const next of edges.get(key) || []) if (!visited.has(next)) pending.push(next);
    }
    if (component.length < 2) continue;
    // Cyclic bases are invalid dependency data, so do not advertise a stack for them.
    let cycle = false;
    const depths = new Map<string, number>();
    for (const key of component) {
      const seen = new Set<string>();
      let current: string | undefined = key,
        depth = 0;
      while (current && parents.has(current)) {
        if (seen.has(current)) {
          cycle = true;
          break;
        }
        seen.add(current);
        current = parents.get(current);
        depth++;
      }
      depths.set(key, depth);
    }
    if (cycle) continue;
    const key = `${byRef.get(start)?.item.repo}:branch:${component.toSorted()[0]}`;
    for (const member of component) result.set(member, { key, depth: depths.get(member) || 0 });
  }
  return result;
}
export function groupEntries(entries: Entry[], mode: string, estate: Entry[]): QueueGroup[] {
  const groups = new Map<string, QueueGroup>();
  const inferred = mode === 'stacks' ? inferredChains(estate) : new Map<string, BranchChain>();
  for (const entry of entries) {
    const item = entry.item;
    let key = ref(item),
      title = '',
      description = '',
      icon: IconName = 'stack';
    if (mode === 'repository') {
      key = item.repo;
      title = item.repo;
      icon = 'folder';
    } else if (mode === 'action') {
      const signal = signals(entry).find((signal) => signal.action);
      key = signal?.key || 'waiting';
      title = signal ? actionNames[signal.key] || signal.label : 'No immediate action';
      icon = signal?.icon || 'clock';
    } else if (mode === 'stacks' && item.kind === 'pr' && item.facts?.stack) {
      key = `${item.repo}:stack:${item.facts.stack.number}`;
      title = `Stack #${item.facts.stack.number}`;
      description = `${item.repo} · ${item.facts.stack.size} PRs · base ${item.facts.stack.base}`;
    } else if (mode === 'stacks' && inferred.has(ref(item))) {
      key = inferred.get(ref(item))?.key || key;
      title = 'Branch chain';
      description = `${item.repo} · inferred from base branches`;
    }
    const existing = groups.get(key);
    if (existing) existing.entries.push(entry);
    else
      groups.set(key, { key, title, description, icon, entries: [entry], grouped: Boolean(title) });
  }
  for (const group of groups.values())
    if (mode === 'stacks' && group.grouped)
      group.entries.sort(
        (a, b) =>
          (a.item.facts?.stack?.position ?? inferred.get(ref(a.item))?.depth ?? 0) -
            (b.item.facts?.stack?.position ?? inferred.get(ref(b.item))?.depth ?? 0) ||
          a.item.number - b.item.number,
      );
  return [...groups.values()];
}
