import { Icon } from './Icon';
import { SignalChip } from './Signals';
import { failedStates, groupEntries, pendingStates, signals } from './attention';
import type { Signal } from './attention';
import { githubUrl, ref } from './model';
import type { Entry } from './model';
import { itemUrl, navigate } from './router';
import type { QueueView, Route } from './router';

export function GitHubFacts({
  entry,
  estate,
  route,
}: {
  entry: Entry;
  estate: Entry[];
  route: Route;
}) {
  const { item } = entry,
    facts = item.facts;
  const status = signals(entry);
  const stack = facts?.stack;
  const group = groupEntries(estate, 'stacks', estate).find((group) =>
    group.entries.some((member) => ref(member.item) === ref(item)),
  );
  const members = stack?.members.length
    ? stack.members
    : group?.grouped
      ? group.entries.map((entry, index) => ({
          number: entry.item.number,
          title: entry.item.title,
          state: entry.item.state,
          position: entry.item.facts?.stack?.position ?? index + 1,
        }))
      : [];
  const native = Boolean(stack);
  const selectedLink = (number: number) =>
    itemUrl(route.page as QueueView, item.repo, number, route.query, route.repository);
  const actionUrl = (signal: Signal) => {
    if (signal.key.startsWith('checks')) return `${githubUrl(item)}/checks`;
    if (signal.key === 'review' && item.kind === 'pr') return githubUrl(item, true);
    return githubUrl(item);
  };
  const checkSignal = (name: string, state: string): Signal => ({
    key: name,
    label: state.toLowerCase().replaceAll('_', ' '),
    tone: failedStates.has(state)
      ? 'danger'
      : state === 'SUCCESS'
        ? 'success'
        : pendingStates.has(state)
          ? 'info'
          : 'quiet',
    icon: failedStates.has(state) ? 'alert' : state === 'SUCCESS' ? 'check' : 'clock',
    action: false,
  });
  const safeCheckUrl = (url: string) =>
    /^https?:\/\//i.test(url) ? url : `${githubUrl(item)}/checks`;
  return (
    <section className="github-facts" aria-label="GitHub actions and context">
      <div className="action-signals" aria-label="Action signals">
        {status.map((signal) =>
          signal.action && signal.key !== 'unknown' ? (
            <a key={signal.key} href={actionUrl(signal)} target="_blank" rel="noopener noreferrer">
              <SignalChip signal={signal} />
              <span className="sr-only"> on GitHub</span>
            </a>
          ) : (
            <SignalChip key={signal.key} signal={signal} />
          ),
        )}
      </div>
      {members.length && (native || group?.grouped) ? (
        <details className="stack-context">
          <summary>
            <Icon name="stack" />
            <strong>{stack ? `Stack #${stack.number}` : 'Branch chain'}</strong>
            <span>
              {stack ? `Part ${stack.position} of ${stack.size}` : `${members.length} PRs`}
            </span>
          </summary>
          <p>
            {native
              ? `GitHub registered stack · base ${stack?.base}`
              : 'Inferred from base branches; not a registered GitHub stack.'}
          </p>
          <ol>
            {members.map((member) => {
              const known = estate.some(
                (entry) => entry.item.repo === item.repo && entry.item.number === member.number,
              );
              const href = known
                ? selectedLink(member.number)
                : `https://github.com/${item.repo}/pull/${member.number}`;
              return (
                <li
                  key={member.number}
                  data-current={member.number === item.number}
                  data-state={member.state}
                >
                  <span className="stack-position">{member.position}</span>
                  <Icon name={member.state === 'MERGED' ? 'check' : 'pull'} />
                  <a
                    href={href}
                    aria-current={member.number === item.number ? 'true' : undefined}
                    onClick={(event) => {
                      if (
                        !known ||
                        event.metaKey ||
                        event.ctrlKey ||
                        event.shiftKey ||
                        event.altKey
                      )
                        return;
                      event.preventDefault();
                      navigate(href);
                    }}
                  >
                    #{member.number} {member.title}
                  </a>
                  <span className="stack-member-state">{member.state.toLowerCase()}</span>
                </li>
              );
            })}
          </ol>
          {stack && members.length < stack.size ? (
            <p>
              Showing {members.length} of {stack.size} stack members in the saved snapshot.
            </p>
          ) : null}
        </details>
      ) : stack ? (
        <p className="context-note">
          <Icon name="stack" />
          Stack #{stack.number} · part {stack.position} of {stack.size}. Check GitHub to load
          members.
        </p>
      ) : null}
      {facts && item.kind === 'pr' ? (
        <details className="check-details" open={failedStates.has(facts.checks_state) || undefined}>
          <summary>
            <Icon
              name={
                failedStates.has(facts.checks_state)
                  ? 'alert'
                  : facts.checks_state === 'SUCCESS'
                    ? 'check'
                    : 'clock'
              }
            />
            <strong>Checks</strong>
            <span data-tone={failedStates.has(facts.checks_state) ? 'danger' : 'quiet'}>
              {facts.checks_state.toLowerCase()}
            </span>
          </summary>
          {facts.checks.length ? (
            <ul className="check-list">
              {facts.checks.map((check, index) => (
                <li key={`${check.name}:${index}`}>
                  <SignalChip signal={checkSignal(check.name, check.state)} />
                  <a href={safeCheckUrl(check.url)} target="_blank" rel="noopener noreferrer">
                    {check.name}
                    <Icon name="external" />
                  </a>
                </li>
              ))}
            </ul>
          ) : (
            <p className="context-note">
              {facts.checks_state === 'UNKNOWN'
                ? 'No check rollup is available. This does not confirm passing checks.'
                : 'Individual jobs load when this item is checked.'}
            </p>
          )}
          {facts.checks_total !== null && facts.checks.length < facts.checks_total ? (
            <p className="context-note">
              Showing {facts.checks.length} of {facts.checks_total} checks. Open GitHub for the
              complete list.
            </p>
          ) : null}
        </details>
      ) : null}
      {facts ? (
        <details className="item-context">
          <summary>
            <Icon name="user" />
            <strong>Ownership & linked work</strong>
            <span>{facts.assignees.length ? facts.assignees.join(', ') : 'Unassigned'}</span>
          </summary>
          <dl>
            <div>
              <dt>Author</dt>
              <dd>{facts.author || 'Unknown'}</dd>
            </div>
            <div>
              <dt>Assignees</dt>
              <dd>{facts.assignees.join(', ') || 'Unassigned'}</dd>
            </div>
            {facts.reviewers.length ? (
              <div>
                <dt>Review requested from</dt>
                <dd>{facts.reviewers.join(', ')}</dd>
              </div>
            ) : null}
            {facts.milestone ? (
              <div>
                <dt>Milestone</dt>
                <dd>{facts.milestone}</dd>
              </div>
            ) : null}
            {facts.base ? (
              <div>
                <dt>Branches</dt>
                <dd>
                  <code>{facts.head}</code> → <code>{facts.base}</code>
                </dd>
              </div>
            ) : null}
            {facts.state_reason ? (
              <div>
                <dt>Resolution</dt>
                <dd>{facts.state_reason.toLowerCase().replaceAll('_', ' ')}</dd>
              </div>
            ) : null}
          </dl>
          <div className="github-labels">
            {facts.labels.map((label) => (
              <span key={label}>
                <Icon name="tag" />
                {label}
              </span>
            ))}
          </div>
          {facts.related.length ? (
            <ul className="linked-work">
              {facts.related.map((related) => (
                <li key={`${related.repo}#${related.number}`}>
                  <Icon name={related.kind === 'pr' ? 'pull' : 'issue'} />
                  <a
                    href={`https://github.com/${related.repo}/${related.kind === 'pr' ? 'pull' : 'issues'}/${related.number}`}
                    target="_blank"
                    rel="noopener noreferrer"
                  >
                    {related.repo}#{related.number} {related.title}
                  </a>
                  <span>{related.state.toLowerCase()}</span>
                </li>
              ))}
            </ul>
          ) : null}
          {facts.partial.length ? (
            <p className="context-note">
              Partial lists: {facts.partial.join(', ')}. Open GitHub for complete details.
            </p>
          ) : null}
        </details>
      ) : null}
    </section>
  );
}
