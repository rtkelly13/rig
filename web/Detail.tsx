import { Badge, Button, TextArea } from '@rtkelly-labs/design-system';
import { useEffect, useState } from 'react';
import {
  age,
  githubUrl,
  hasNewDiscussion,
  isArchived,
  isDeferred,
  isUnread,
  lastDecision,
  readiness,
  reviewState,
  when,
} from './model';
import type { Action, Entry, Event } from './model';
import type { QueueState } from './useQueue';
import { Icon } from './Icon';
import { updateQuery } from './router';
import { MarkdownContent } from './MarkdownContent';
import { GitHubFacts } from './GitHubFacts';
import type { Route } from './router';

const labels: Record<string, string> = {
  opened: 'Marked read',
  unread: 'Marked unread',
  archived: 'Archived locally',
  unarchived: 'Moved to inbox',
  review_requested: 'Review requested',
  reviewed: 'Reviewed this head',
  changes_requested: 'Changes requested',
  deferred: 'Snoozed until resumed',
  resumed: 'Resumed',
  note: 'Note saved',
  'github:approved': 'Approved on GitHub',
  'github:commented': 'Commented on GitHub',
  'github:changes_requested': 'Requested changes on GitHub',
};
function EventRow({ event, sourceUrl }: { event: Event; sourceUrl: string }) {
  const [messageOpen, setMessageOpen] = useState(false);
  let label = labels[event.action] || event.action.replace('github:', '');
  if (event.action === 'observed') {
    try {
      const observed: { state?: string; head?: string } = JSON.parse(event.note);
      label =
        observed.state === 'MERGED'
          ? 'Merge observed'
          : observed.state === 'CLOSED'
            ? 'Closure observed'
            : 'GitHub state observed';
    } catch {
      label = 'GitHub state observed';
    }
  }
  const body = event.action !== 'observed' && event.note;
  return (
    <li className="event-row">
      <span className="feed-avatar" aria-hidden="true">
        {event.actor === 'local'
          ? 'Y'
          : event.actor === 'rig'
            ? 'R'
            : event.actor.slice(0, 1).toUpperCase()}
      </span>
      <div>
        <div className="event-header">
          <strong>
            {event.actor === 'local' ? 'You' : event.actor === 'rig' ? 'Rig' : event.actor}
          </strong>
          <span
            className="feed-event-kind"
            data-tone={
              event.action.includes('changes_requested')
                ? 'warning'
                : event.action.includes('approved')
                  ? 'success'
                  : 'info'
            }
          >
            <Icon
              name={
                event.action.includes('approved')
                  ? 'check'
                  : event.action.includes('review') || event.action.includes('changes_requested')
                    ? 'review'
                    : 'message'
              }
            />
            {label}
          </span>
          <time dateTime={event.at} title={event.at}>
            {when(event.at)}
          </time>
        </div>
        <p className="event-meta">
          {event.head ? 'Commit' : ''}
          {event.head ? ` · ${event.head.slice(0, 8)}` : ''}
        </p>
        {body ? (
          event.actor.includes('[bot]') || event.note.length > 600 ? (
            <details onToggle={(event) => setMessageOpen(event.currentTarget.open)}>
              <summary>Show message</summary>
              {messageOpen ? (
                <div className="event-body">
                  <MarkdownContent sourceUrl={sourceUrl}>{event.note}</MarkdownContent>
                </div>
              ) : null}
            </details>
          ) : (
            <div className="event-body">
              <MarkdownContent sourceUrl={sourceUrl}>{event.note}</MarkdownContent>
            </div>
          )
        ) : null}
      </div>
    </li>
  );
}

export function Detail({
  entry,
  state,
  allActivity,
  route,
}: {
  entry: Entry;
  state: QueueState;
  allActivity: boolean;
  route: Route;
}) {
  const { item } = entry;
  const storageKey = `rig:triage:draft:${item.repo}#${item.number}`;
  const [note, setNote] = useState(() => {
    try {
      return sessionStorage.getItem(storageKey) || '';
    } catch {
      return '';
    }
  });
  const [copied, setCopied] = useState(false);
  const [descriptionOpen, setDescriptionOpen] = useState(true);
  const [savingAction, setSavingAction] = useState<Action | null>(null);
  useEffect(() => {
    try {
      note ? sessionStorage.setItem(storageKey, note) : sessionStorage.removeItem(storageKey);
    } catch {
      /* Storage may be unavailable. */
    }
  }, [storageKey, note]);
  const decision = lastDecision(entry);
  const changed = Boolean(decision && decision.head !== item.head);
  const discussion = Boolean(decision && hasNewDiscussion(entry, decision.at));
  const open = item.state === 'OPEN' && item.in_scope;
  const events = [...entry.events]
    .filter((event) => allActivity || !event.actor.includes('[bot]'))
    .sort((a, b) => b.at.localeCompare(a.at) || b.id - a.id);
  const conversation = events
    .filter((event) => event.action.startsWith('github:') || event.action === 'note')
    .toSorted((a, b) => a.at.localeCompare(b.at) || a.id - b.id);
  const history = events.filter(
    (event) => !event.action.startsWith('github:') && event.action !== 'note',
  );
  const record = async (action: Action) => {
    setSavingAction(action);
    const saved = await state.perform(
      '/interact',
      { repo: item.repo, number: item.number, head: item.head, action, note },
      {
        note: 'Note saved.',
        deferred: 'Snoozed until you resume it.',
        resumed: 'Returned to the inbox.',
        archived: 'Archived locally. New commits or human replies can bring it back.',
        unarchived: 'Moved to the inbox.',
        opened: 'Marked read.',
        unread: 'Marked unread.',
        reviewed: 'Reviewed this head. Waiting for an update or merge.',
        changes_requested: 'Waiting for changes.',
        review_requested: 'Review requested.',
      }[action],
    );
    if (saved && !['opened', 'unread'].includes(action))
      setNote((current) => (current === note ? '' : current));
    setSavingAction(null);
  };
  const poll = () =>
    state.perform('/watch', { repo: item.repo, number: item.number }, 'GitHub state checked.');
  return (
    <article className="detail-pane" aria-labelledby="selected-title">
      <div className="inbox-actions" aria-label="Inbox actions">
        {open ? (
          <>
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button"
              disabled={Boolean(state.pending)}
              onClick={() => record(isArchived(entry) ? 'unarchived' : 'archived')}
            >
              {isArchived(entry) ? 'Move to inbox' : 'Archive'}
            </Button>
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button"
              disabled={Boolean(state.pending)}
              onClick={() => record(isUnread(entry) ? 'opened' : 'unread')}
            >
              {isUnread(entry) ? 'Mark read' : 'Mark unread'}
            </Button>
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button"
              disabled={Boolean(state.pending)}
              onClick={() => record(isDeferred(entry) ? 'resumed' : 'deferred')}
            >
              {isDeferred(entry) ? 'Resume' : 'Snooze'}
            </Button>
          </>
        ) : (
          <span>Your completed work stays searchable.</span>
        )}
      </div>
      <div className="detail-topline">
        <span className="detail-ref">
          {item.repo} <span>#{item.number}</span>
        </span>
        <Badge accent={changed ? 'warning' : 'quiet'}>{reviewState(entry)}</Badge>
      </div>
      <h2 id="selected-title">
        <a href={githubUrl(item)} target="_blank" rel="noopener noreferrer">
          {item.title}
        </a>
      </h2>
      <div className="detail-links">
        <Button
          className="open-pr-button"
          href={githubUrl(item)}
          target="_blank"
          rel="noopener noreferrer"
          variant="primary"
          size="sm"
        >
          Open {item.kind === 'pr' ? 'PR' : 'issue'} on GitHub <Icon name="external" />
        </Button>
        {item.kind === 'pr' ? (
          <a href={githubUrl(item, true)} target="_blank" rel="noopener noreferrer">
            Read diff <Icon name="external" />
          </a>
        ) : null}
      </div>
      <dl className="detail-metrics">
        <div>
          <dt>{item.kind === 'pr' ? 'Merge state' : 'State'}</dt>
          <dd>
            <span className={`status-dot status-${item.readiness}`} />
            {item.kind === 'pr' ? readiness[item.readiness] : item.state.toLowerCase()}
          </dd>
        </div>
        <div>
          <dt>{item.kind === 'pr' ? 'Changed lines' : 'Priority'}</dt>
          <dd>
            {item.kind === 'pr'
              ? item.lines.toLocaleString()
              : item.priority < 4
                ? `P${item.priority}`
                : 'Unlabelled'}
          </dd>
        </div>
        <div>
          <dt>Waiting since</dt>
          <dd>
            {new Date(item.created).toLocaleDateString(undefined, {
              month: 'short',
              day: 'numeric',
            })}
            <small>{age(item.created)}</small>
          </dd>
        </div>
      </dl>
      {item.head ? (
        <div className="head-commit">
          <span>Head</span>
          <code title={item.head}>{item.head.slice(0, 12)}</code>
          <button
            aria-label="Copy head SHA"
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(item.head);
                setCopied(true);
                setTimeout(() => setCopied(false), 2000);
              } catch {
                setCopied(false);
              }
            }}
          >
            <Icon name={copied ? 'check' : 'copy'} />
            {copied ? 'Copied' : 'Copy'}
          </button>
        </div>
      ) : null}
      <GitHubFacts entry={entry} estate={state.entries} route={route} />
      <section className="item-description" aria-labelledby="description-heading">
        <div className="description-header">
          <h3 id="description-heading">
            <Icon name="book" />
            {item.kind === 'pr' ? 'PR description' : 'Issue description'}
          </h3>
          <span>{item.facts?.author ? `By ${item.facts.author}` : 'From GitHub'}</span>
          <button
            className="text-button"
            aria-expanded={descriptionOpen}
            aria-controls="description-content"
            onClick={() => setDescriptionOpen(!descriptionOpen)}
          >
            {descriptionOpen ? 'Collapse' : 'Expand'}
          </button>
        </div>
        <div id="description-content" hidden={!descriptionOpen}>
          {item.body == null ? (
            <p className="empty-message">
              {state.pending === '/watch'
                ? 'Loading the description from GitHub…'
                : 'Description is not in the saved snapshot. Use Check GitHub to fetch it.'}
            </p>
          ) : item.body.trim() ? (
            <MarkdownContent sourceUrl={githubUrl(item)}>{item.body}</MarkdownContent>
          ) : (
            <p className="empty-message">No description provided.</p>
          )}
        </div>
      </section>

      {changed || discussion || decision || !open ? (
        <>
          <section
            className={`review-callout ${changed ? 'review-changed' : ''}`}
            aria-label="Review state"
          >
            <div className="callout-heading">
              <Icon name={open ? 'pull' : 'check'} />
              <h3>
                {!open
                  ? reviewState(entry)
                  : changed
                    ? 'A new commit needs your review'
                    : discussion
                      ? 'New discussion needs your attention'
                      : decision
                        ? 'Waiting for the next change'
                        : item.kind === 'pr'
                          ? 'Your review is next'
                          : 'Ready for your triage'}
              </h3>
            </div>
            <p>
              {!open
                ? 'Your notes and decisions remain in the history below.'
                : changed
                  ? 'Your previous decision belongs to an older head. Review this commit before recording another decision.'
                  : discussion
                    ? 'A human reply arrived after your last decision. Read the conversation and decide what happens next.'
                    : decision
                      ? 'Your decision is recorded for this head. Keep this item open while waiting for an update or merge.'
                      : 'Read the changes and checks on GitHub, then record your decision here.'}
            </p>
          </section>
        </>
      ) : null}
      <section className="activity-section" aria-labelledby="activity-heading">
        <div className="section-heading">
          <h3 id="activity-heading">
            <Icon name="message" />
            {item.kind === 'pr' ? 'PR feed' : 'Issue feed'} <span>{conversation.length}</span>
          </h3>
          <button
            className="text-button"
            onClick={() => updateQuery('activity', allActivity ? '' : 'all')}
          >
            {allActivity ? 'Hide bot activity' : 'Show bot activity'}
          </button>
        </div>
        <ol className="activity-list">
          {conversation.map((event) => (
            <EventRow key={event.id} event={event} sourceUrl={githubUrl(item)} />
          ))}
        </ol>
        {!conversation.length ? (
          <p className="empty-message">No discussion yet. Notes and GitHub replies appear here.</p>
        ) : null}
      </section>
      <section className="decision-section" aria-labelledby="decision-heading">
        <h3 id="decision-heading">
          {item.kind === 'pr' ? 'Record your decision' : 'Triage notes'}
        </h3>
        <TextArea
          id="triage-note"
          label="Review notes"
          placeholder="What did you find? What needs to happen next?"
          value={note}
          onChange={(event) => setNote(event.target.value)}
          rows={3}
        />
        <div className="decision-actions">
          {open && item.kind === 'pr' ? (
            <>
              <Button
                size="sm"
                variant="primary"
                pending={savingAction === 'reviewed'}
                disabled={Boolean(state.pending && savingAction !== 'reviewed')}
                onClick={() => record('reviewed')}
              >
                <Icon name="check" />
                Reviewed
              </Button>
              <Button
                size="sm"
                variant="inverse"
                className="quiet-button"
                disabled={Boolean(state.pending)}
                onClick={() => record('changes_requested')}
              >
                Changes requested
              </Button>
            </>
          ) : null}
          <Button
            size="sm"
            variant="inverse"
            className="quiet-button"
            pending={savingAction === 'note'}
            disabled={!note.trim() || Boolean(state.pending && savingAction !== 'note')}
            onClick={() => record('note')}
          >
            Save note
          </Button>
          {open ? (
            <Button
              size="sm"
              variant="inverse"
              className="quiet-button"
              disabled={Boolean(state.pending)}
              onClick={() => record(isDeferred(entry) ? 'resumed' : 'deferred')}
            >
              {isDeferred(entry) ? 'Resume' : 'Snooze'}
            </Button>
          ) : null}
        </div>
        <p className="field-note">
          These are local triage decisions. Post reviews and merge on GitHub.
        </p>
      </section>
      <details className="technical-history">
        <summary>
          Local triage history <span>{history.length}</span>
        </summary>
        <ol className="activity-list">
          {history.map((event) => (
            <EventRow key={event.id} event={event} sourceUrl={githubUrl(item)} />
          ))}
        </ol>
      </details>
      <div className="detail-footer">
        <span>Updated {when(item.updated)}</span>
        <Button
          size="sm"
          variant="inverse"
          className="quiet-button"
          pending={state.pending === '/watch'}
          disabled={Boolean(state.pending && state.pending !== '/watch')}
          onClick={poll}
        >
          <Icon name="refresh" />
          Check GitHub
        </Button>
      </div>
    </article>
  );
}
